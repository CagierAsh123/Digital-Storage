using System;
using System.Collections.Generic;
using System.Text;
using DigitalStorage.AI;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// <b>制作代理</b>：由建筑自己把"面板里选的配方"做出来，殖民者不参与。
    ///
    /// <para><b>控制面是面板，不是工作台</b>（用户拍板）：13×13 范围内的工作台只负责
    /// <b>解锁配方</b>（<see cref="CraftUnlocks"/>），玩家在自己的面板里挑配方、设次数，
    /// 然后代理扣料 / 假装读条 / 产物进核心 —— **全程不用碰工作台**。</para>
    ///
    /// <para><b>一条配方 = 一个 <see cref="CraftPlan"/>，同时在产多少件 = 范围内"能做它且可用"的工作台数</b>
    /// （用户拍板）。每条在产线（<see cref="CraftLine"/>）占一张台子，所以"三个台能做 ⇒ 并发 3"。</para>
    ///
    /// <para><b>取活怎么不重写选料器</b>：把自己那条转瞬即逝的原版 bill 临时换进台子的
    /// <c>BillStack</c>（<see cref="BillStackSwap"/>），调公开入口
    /// <c>WorkGiver_DoBill.JobOnThing</c>，拿到原版亲手挑好的原料后立刻还原。
    /// 详见 <c>docs/实现方案-bill自动化.md</c> 与 obsidian
    /// <c>代码Wiki/rimworld/复用原版bill链-JobOnThing是公开取活入口.md</c>。</para>
    ///
    /// <para><b>耗电</b>（用户拍板）：<c>(300 + Σ范围内工作台耗电) × 超频倍率</c>；
    /// 没有电力组件的工作台按 100W 折算；工作台自己的电**照交**（不豁免）。</para>
    /// </summary>
    public class CompBillAutomation : ThingComp
    {
        // ===================================================================
        // 状态
        // ===================================================================

        /// <summary>面板里的配方列表（**进存档**）。</summary>
        private List<CraftPlan> plans = new List<CraftPlan>();

        /// <summary>本次扫描到的、范围内能承载 bill 的建筑（不存）。</summary>
        private readonly List<Thing> benches = new List<Thing>();

        /// <summary>这些建筑的 def 集合（去重用）。</summary>
        private readonly HashSet<ThingDef> benchDefs = new HashSet<ThingDef>();

        /// <summary>面板"添加配方"菜单的候选（= 范围内台子能做的配方并集，随扫描刷新）。</summary>
        private readonly List<RecipeDef> unlocked = new List<RecipeDef>();

        private Pawn worker;
        private int nextWorkerAttemptTick = -99999;
        private int nextScanTick;
        private bool enabled = true;

        /// <summary>超频档位：0 = 关，1 = 3GHz，2 = 6GHz，3 = 9GHz。</summary>
        private int overclockTier;

        /// <summary>是否在地图上画出 13×13 扫描范围（gizmo 切换，进存档）。</summary>
        private bool showRange;

        /// <summary>扫描范围格子缓存（<see cref="RangeCellsForDrawing"/> 用；建筑不动就不重建）。</summary>
        private List<IntVec3> rangeCells;
        private IntVec3 rangeCachedCenter = IntVec3.Invalid;
        private int rangeCachedRadius = -1;

        /// <summary>缓存的实际耗电（W）；每 tick 只与 <c>PowerOutput</c> 比一次。</summary>
        private float cachedWatts = -1f;

        // 三个 gizmo 的图标（源文件是 SVG，见 Tools/svg2png/；见 GizmoTex 的说明）
        private Texture2D texToggle;
        private Texture2D texOverclock;
        private Texture2D texRange;

        /// <summary>台子认领表按图分桶，放手时要用同一个 map 引用。</summary>
        private Map slotsMap;

        // 左上角提示的聚合缓冲
        private readonly Dictionary<string, int> pendingStored = new Dictionary<string, int>();
        private readonly Dictionary<string, int> pendingDropped = new Dictionary<string, int>();
        private int lastMessageTick = -99999;

        private int completedCount;
        private int droppedCount;

        // 复用的临时表
        private readonly HashSet<Thing> usedBenches = new HashSet<Thing>();
        private readonly List<CraftPlan> demandPlans = new List<CraftPlan>();

        public CompProperties_BillAutomation Props
        {
            get { return (CompProperties_BillAutomation)props; }
        }

        /// <summary>给面板看的配方列表。</summary>
        internal IList<CraftPlan> PlansForReading
        {
            get { return plans; }
        }

        /// <summary>给"添加配方"窗口看的候选配方。</summary>
        internal IList<RecipeDef> UnlockedRecipes
        {
            get { return unlocked; }
        }

        /// <summary>是否把扫描范围画在地图上（gizmo 切换；<see cref="MapComponent_CraftRange"/> 每帧读它）。</summary>
        public bool ShowRange
        {
            get { return showRange; }
        }

        /// <summary>
        /// 扫描范围的格子（13×13 方形，与 <see cref="CompProperties_BillAutomation.scanRadius"/> 同源）。
        ///
        /// <para><b>缓存</b>：地图绘制阶段每帧都会问一次，但建筑不会动、半径也不变
        /// ⇒ 只在"位置/半径变了"时重建那 169 个格子。</para>
        /// </summary>
        public List<IntVec3> RangeCellsForDrawing()
        {
            int radius = Math.Max(0, Props.scanRadius);
            IntVec3 center = parent.PositionHeld;

            if (rangeCells == null || rangeCachedCenter != center || rangeCachedRadius != radius)
            {
                rangeCachedCenter = center;
                rangeCachedRadius = radius;
                CellRect rect = CellRect.CenteredOn(center, radius);
                if (rangeCells == null) rangeCells = new List<IntVec3>(rect.Area);
                else rangeCells.Clear();
                foreach (IntVec3 c in rect)
                {
                    rangeCells.Add(c);
                }
            }
            return rangeCells;
        }

        public int CompletedCount
        {
            get { return completedCount; }
        }

        public int DroppedCount
        {
            get { return droppedCount; }
        }

        // ===================================================================
        // 开关 / 电力 / 超频
        // ===================================================================

        public bool Powered
        {
            get
            {
                CompPowerTrader p = parent.TryGetComp<CompPowerTrader>();
                return p == null || p.PowerOn;
            }
        }

        public bool CanWork
        {
            get { return parent.Spawned && !parent.Destroyed && Powered && enabled; }
        }

        public bool Enabled
        {
            get { return enabled; }
            set
            {
                if (enabled == value) return;
                enabled = value;
                ReleaseAll();
                RecalcWatts();
            }
        }

        public int OverclockTier
        {
            get { return overclockTier; }
            set
            {
                int v = Mathf.Clamp(value, 0, 3);
                if (v == overclockTier) return;
                overclockTier = v;
                RecalcWatts();
            }
        }

        /// <summary>超频速度倍率（用户拍板：1 / 3 / 6 / 9）。</summary>
        public float OverclockSpeedMult
        {
            get { return overclockTier <= 0 ? 1f : (overclockTier == 1 ? 3f : (overclockTier == 2 ? 6f : 9f)); }
        }

        /// <summary>超频耗电倍率（用户拍板：1 / 9 / 27 / 81）。</summary>
        public float OverclockPowerMult
        {
            get { return overclockTier <= 0 ? 1f : (overclockTier == 1 ? 9f : (overclockTier == 2 ? 27f : 81f)); }
        }

        /// <summary>当前实际耗电（W），面板/检视栏用。</summary>
        public float CurrentWatts
        {
            get
            {
                float w = Props.basePowerWatts;
                if (enabled) w += BenchWatts();
                return w * OverclockPowerMult;
            }
        }

        /// <summary>范围内工作台的耗电合计（没有电力组件的按 <c>benchWithoutPowerWatts</c> 折算）。</summary>
        public float BenchWatts()
        {
            float sum = 0f;
            for (int i = 0; i < benches.Count; i++)
            {
                Thing bench = benches[i];
                if (bench == null || bench.Destroyed) continue;
                CompPowerTrader p = bench.TryGetComp<CompPowerTrader>();
                sum += (p == null) ? Props.benchWithoutPowerWatts : p.Props.PowerConsumption;
            }
            return sum;
        }

        // ===================================================================
        // 主循环
        // ===================================================================

        public override void CompTick()
        {
            if (!CanWork)
            {
                ReleaseAll();
                return;
            }

            Map map = parent.Map;
            if (map == null) return;

            int now = Find.TickManager.TicksGame;

            // ① 扫描工作台（低频）+ 刷新"能做哪些配方"
            if (now >= nextScanTick)
            {
                nextScanTick = now + Math.Max(1, Props.scanIntervalTicks);
                RescanBenches(map);
                SyncLines(map);
                RecalcWatts();
            }

            if (cachedWatts < 0f) RecalcWatts();
            ReassertPower();

            // 左上角提示要在这里刷（列表可能刚好清空；放在早退之后最后一条提示就发不出去）
            FlushMessages(now);

            if (plans.Count == 0) return;

            Pawn w = Worker;
            if (w == null) return;

            AdvanceAll(w, map, now);
            ProbeAll(map, w, now);
            UpdateVisuals(map);
        }

        /// <summary>
        /// 扫"以建筑为中心的 13×13 方形"（<see cref="CompProperties_BillAutomation.scanRadius"/>）里
        /// 能承载 bill 的建筑。
        ///
        /// <para>候选来源用 <c>listerThings.ThingsInGroup(PotentialBillGiver)</c> —— 原版
        /// <c>WorkGiver_DoBill.ShouldSkip:128</c> 用的就是这条（有 lister 分支，不是全图遍历）。
        /// **只认 <c>Building</c>**：Pawn / Corpse 也是 <c>IBillGiver</c>（手术、屠宰），
        /// 但它们不是"工作台"，也不该出现在制作面板里。</para>
        /// </summary>
        private void RescanBenches(Map map)
        {
            if (slotsMap != null && slotsMap != map) ReleaseAll();
            slotsMap = map;

            benches.Clear();
            benchDefs.Clear();

            int radius = Math.Max(0, Props.scanRadius);
            CellRect rect = CellRect.CenteredOn(parent.PositionHeld, radius);
            List<Thing> all = map.listerThings.ThingsInGroup(ThingRequestGroup.PotentialBillGiver);

            for (int i = 0; i < all.Count; i++)
            {
                Thing t = all[i];
                if (t == null || t.Destroyed || !t.Spawned || t == parent) continue;
                if (!(t is Building)) continue;
                if (!(t is IBillGiver)) continue;
                if (!rect.Contains(t.PositionHeld)) continue;
                // 能做东西才收（AllRecipes 见 CanCraft 的注释：不能只看 def.recipes）
                List<RecipeDef> rs = t.def.AllRecipes;
                if (rs == null || rs.Count == 0) continue;

                benches.Add(t);
                benchDefs.Add(t.def);
            }

            CraftUnlocks.MenuRecipes(benchDefs, unlocked);
        }

        // ===================================================================
        // 生产线调度
        // ===================================================================

        /// <summary>
        /// 让"在产线数"对齐"可用台子数"（用户拍板的并发口径）。
        /// <list type="number">
        /// <item>先释放：配方被挂起/做完、台子没了/断电/不再能做该配方；</item>
        /// <item>再补足：从空闲台子里给活跃配方配线，受 <c>maxSlots</c> 与认领表限制。</item>
        /// </list>
        /// 台子认领（<see cref="BillBenchClaims"/>）保证**一台工作台同一时间只服务一条线**，
        /// 也保证两台制作代理范围重叠时不会抢同一台。
        /// </summary>
        private void SyncLines(Map map)
        {
            for (int p = plans.Count - 1; p >= 0; p--)
            {
                CraftPlan plan = plans[p];
                for (int i = plan.lines.Count - 1; i >= 0; i--)
                {
                    CraftLine line = plan.lines[i];
                    Thing b = line.Bench;
                    if (!plan.Maintained || b == null || b.Destroyed || !b.Spawned
                        || !BenchUsable(b) || !CanCraft(b, plan.recipe))
                    {
                        ReleaseLine(plan, i);
                    }
                }
            }

            usedBenches.Clear();
            for (int p = 0; p < plans.Count; p++)
            {
                CraftPlan plan = plans[p];
                for (int i = 0; i < plan.lines.Count; i++)
                {
                    if (plan.lines[i].Bench != null) usedBenches.Add(plan.lines[i].Bench);
                }
            }

            int free = Math.Max(1, Props.maxSlots) - TotalLines();
            if (free <= 0) return;

            // 先刷一遍"这一轮该不该开工"，顺便收集有需求的计划
            //（RefreshTargetStateIfStale 会跑原版计数器，每个计划一次就够 —— 别在发台循环里重复求值）
            demandPlans.Clear();
            for (int p = 0; p < plans.Count; p++)
            {
                CraftPlan plan = plans[p];
                if (!plan.Maintained) continue;

                // 维持数量模式：先按原版计数器数一遍，决定这一轮要不要开新活
                //（达标 ⇒ wantsWork=false ⇒ 不再配新线；**在产的那几件照做**，与原版 paused 语义一致）
                RefreshTargetStateIfStale(plan, map);
                if (!plan.CanStartNewWork) continue;

                demandPlans.Add(plan);
            }
            if (demandPlans.Count == 0) return;

            // B1 / 对应第三方补丁 FIX-12（审计 2026-10-17）：**轮转发台**。
            //
            // 老实现是"按 plans 顺序先到先得，一个计划把空闲的同类台全部占线"
            // —— `want = FreeBenchCountFor(plan.recipe)` 只数有几张空台，**不看还差几件要做**
            // ⇒ 做 1 件也把 3 台占满，后面的计划永远等不到台子（台子越多越明显）。
            // 现在每一轮给每个还想开线的计划各发一张台，直到台子发完或没人还想要；
            // 单个计划的目标线数再受 DemandLines（还剩几件要做）约束：做 1 件就只占 1 台。
            bool progress = true;
            while (free > 0 && progress)
            {
                progress = false;
                for (int i = 0; i < demandPlans.Count && free > 0; i++)
                {
                    CraftPlan plan = demandPlans[i];
                    if (plan.lines.Count >= DemandLines(plan)) continue;

                    Thing bench = PickBench(plan.recipe);
                    if (bench == null) continue;   // 这个配方在自己范围里没台子了

                    if (!BillBenchClaims.TryClaim(map, bench, this))
                    {
                        usedBenches.Add(bench);   // 别的制作代理占了 ⇒ 本轮别再挑它
                        continue;
                    }
                    usedBenches.Add(bench);
                    plan.lines.Add(MakeLine(plan, bench));
                    free--;
                    progress = true;
                }
            }
        }

        /// <summary>
        /// 这个计划这一轮最多该占几条线（B1/FIX-12：做 1 件不该占 3 台）。
        /// 无限订单没有上限；次数模式按 <c>remaining</c>；维持数量按"离目标还差几件"。
        /// 最小 1 —— 在产的那几件必须能继续做（与原版 paused 不打断已开工的语义一致）。
        /// </summary>
        private static int DemandLines(CraftPlan plan)
        {
            if (plan.mode == CraftPlan.ModeForever) return int.MaxValue;
            if (plan.mode == CraftPlan.ModeCount) return Math.Max(1, plan.remaining);
            return Math.Max(1, plan.targetCount - plan.countedCount);
        }

        private CraftLine MakeLine(CraftPlan plan, Thing bench)
        {
            CraftLine line = new CraftLine();
            line.Plan = plan;
            line.Bench = bench;

            // 我们自己造一条原版 bill：只用于借原版选料器（换进台子一瞬），次数由 CraftPlan 自己记。
            // repeatMode 设成 Forever 是为了让原版 ShouldDoNow 恒真（我们的次数逻辑在 plan 上）。
            Bill_Production bill = new Bill_Production(plan.recipe);
            bill.repeatMode = BillRepeatModeDefOf.Forever;

            IBillGiver giver = bench as IBillGiver;
            BillStack stack = new BillStack(giver);
            stack.AddBill(bill);        // 顺带把 bill.billStack 指过去（Bill.Map / DeletedOrDereferenced 要用）
            line.Bill = bill;
            line.TempStack = stack;
            return line;
        }

        private void ReleaseLine(CraftPlan plan, int i)
        {
            CraftLine line = plan.lines[i];
            if (slotsMap != null) BillBenchClaims.Release(slotsMap, line.Bench, this);
            line.ClearAll();
            plan.lines.RemoveAt(i);
        }

        /// <summary>放手全部（断电 / 关掉 / 拆除 / 换图）。</summary>
        public void ReleaseAll()
        {
            for (int p = 0; p < plans.Count; p++)
            {
                CraftPlan plan = plans[p];
                for (int i = plan.lines.Count - 1; i >= 0; i--) plan.lines[i].ClearAll();
                plan.lines.Clear();
            }
            if (slotsMap != null) BillBenchClaims.ReleaseAll(slotsMap, this);
            slotsMap = null;
            benches.Clear();
            usedBenches.Clear();
            demandPlans.Clear();
            pendingStored.Clear();
            pendingDropped.Clear();
        }

        private int TotalLines()
        {
            int n = 0;
            for (int p = 0; p < plans.Count; p++) n += plans[p].lines.Count;
            return n;
        }

        // ===================================================================
        // 查询（调度 + 面板共用，避免两套判据漂移）
        // ===================================================================

        /// <summary>这台工作台现在能不能用来开工（原版口径：供电 + 燃料 + 未故障）。</summary>
        internal static bool BenchUsable(Thing bench)
        {
            IBillGiver giver = bench as IBillGiver;
            if (giver == null) return false;
            if (!bench.Spawned || bench.Destroyed) return false;
            return giver.CurrentlyUsableForBills() && !bench.IsBurning();
        }

        /// <summary>这种建筑能不能做这个配方（= 台子解锁配方的判据，与菜单同源）。</summary>
        internal static bool CanCraft(Thing bench, RecipeDef recipe)
        {
            if (bench == null || recipe == null || bench.def == null) return false;
            // AllRecipes：合并"XML 显式 <recipes>"与"产品 def 的 recipeMaker ⇒ recipeUsers"两路。
            // 用 def.recipes 会漏掉后者（工作台的配方绝大多数在后一路）。
            List<RecipeDef> list = bench.def.AllRecipes;
            if (list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i] == recipe) return true;
            }
            return false;
        }

        /// <summary>范围内能做这个配方的工作台数量（**不管可用性**，面板显示"共几台"用）。</summary>
        internal int BenchTotalFor(RecipeDef recipe)
        {
            int n = 0;
            for (int i = 0; i < benches.Count; i++)
            {
                if (CanCraft(benches[i], recipe)) n++;
            }
            return n;
        }

        /// <summary>范围内"能做且可用"的工作台数量（面板显示 + 并发上限）。</summary>
        internal int UsableBenchCountFor(RecipeDef recipe)
        {
            int n = 0;
            for (int i = 0; i < benches.Count; i++)
            {
                if (CanCraft(benches[i], recipe) && BenchUsable(benches[i])) n++;
            }
            return n;
        }

        /// <summary>
        /// 挑一张能做这个配方、可用且本轮还没被占的台子。
        /// （B1 之后"还能配几条线"由 <see cref="DemandLines"/> 决定，
        /// 不再用"数一数有几张空台"——那个判据正是贪心占台的原因。）
        /// </summary>
        private Thing PickBench(RecipeDef recipe)
        {
            for (int i = 0; i < benches.Count; i++)
            {
                Thing b = benches[i];
                if (!CanCraft(b, recipe) || !BenchUsable(b)) continue;
                if (usedBenches.Contains(b)) continue;
                return b;
            }
            return null;
        }

        // ===================================================================
        // 推进 + 结算
        // ===================================================================

        private void AdvanceAll(Pawn w, Map map, int now)
        {
            float speedMult = Math.Max(0.01f, Props.workSpeedMult) * OverclockSpeedMult;

            // 整个"干活"阶段借一次地图：里面会跑原版 StatPart 链（速度/效率）、品质生成、
            // bill 通知回调、RecordsUtility…它们都可能读 pawn.Map / MapHeld ——
            // 2026-10-03 实机就因为"出作用域后调了这类东西"炸过 ArgumentNullException（null 字典键）。
            // 逐任务借还是整个 tick 借一次？与 CompDigitalWorker 同一口径：**整个 tick 一次**。
            DigitalWorkerScope.Enter(w, map, parent.PositionHeld);
            try
            {
                for (int p = 0; p < plans.Count; p++)
                {
                    CraftPlan plan = plans[p];
                    for (int i = plan.lines.Count - 1; i >= 0; i--)
                    {
                        CraftLine line = plan.lines[i];
                        if (!line.HasWork) continue;

                        if (!StillValid(plan, line, map))
                        {
                            line.ClearWork();
                            line.NextAcquireTick = 0;
                            line.BlockKey = null;
                            continue;
                        }

                        line.WorkLeft -= Math.Max(0f, line.BaseRate) * speedMult;

                        try
                        {
                            line.Bill.Notify_PawnDidWork(w);   // 原版 DoRecipeWork:104（Bill_Production 是空实现）
                        }
                        catch (Exception)
                        {
                            // 子类可能重写；它抛异常不该弄死我们的 tick
                        }

                        if (line.WorkLeft > 0f) continue;

                        CraftResult result;
                        try
                        {
                            result = BillCraftFunnel.TryComplete(this, plan, line, map, w);
                        }
                        catch (Exception e)
                        {
                            Log.ErrorOnce("[DigitalStorage] 制作代理结算异常（丢弃这一轮，料未扣）：" + e, 882244);
                            line.ClearWork();
                            line.NextAcquireTick = now + 300;
                            continue;
                        }

                        if (result == CraftResult.NoBudget)
                        {
                            line.WorkLeft = 0f;      // 活干完了、只差预算 ⇒ 保留进度，下一 tick 再收尾
                            continue;
                        }

                        line.ClearWork();
                        line.NextAcquireTick = 0;

                        if (plan.Done)
                        {
                            NotifyPlanDone(plan);
                            break;                   // 这条配方做完了：本 tick 不再管它的线（SyncLines 会收）
                        }
                    }
                }
            }
            finally
            {
                DigitalWorkerScope.Exit(w);
            }
        }

        /// <summary>这条线这一轮还作不作数（台子 / 电量 / 配方解锁 / 原料都还在）。</summary>
        private static bool StillValid(CraftPlan plan, CraftLine line, Map map)
        {
            // 用 Maintained 而不是 CanStartNewWork：维持数量模式"达标暂停"时，
            // **在产的那几件要让它做完**（原版 paused 也只挡"开始新活"，不打断已开工的）。
            if (plan == null || !plan.Maintained) return false;
            if (!BenchUsable(line.Bench)) return false;

            // 真人接手了这台 ⇒ 让
            if (map.reservationManager != null
                && map.reservationManager.IsReservedByAnyoneOf(line.Bench, Faction.OfPlayer)) return false;

            // 原料还在容器里、数量还够（可能被真人或别的代理拿走了）
            for (int i = 0; i < line.Ingredients.Length; i++)
            {
                Thing t = line.Ingredients[i];
                if (t == null || t.Destroyed) return false;
                Building_StorageCore core = t.ParentHolder as Building_StorageCore;
                if (core == null || core.Map != map) return false;
                if (line.Counts[i] > t.stackCount) return false;
            }
            return true;
        }

        private void ProbeAll(Map map, Pawn w, int now)
        {
            int probes = Math.Max(1, Props.maxProbesPerTick);
            for (int p = 0; p < plans.Count && probes > 0; p++)
            {
                CraftPlan plan = plans[p];
                if (!plan.Maintained) continue;
                if (!plan.CanStartNewWork) continue;   // 维持数量：达标了就别开新活（在产的照做）

                for (int i = 0; i < plan.lines.Count && probes > 0; i++)
                {
                    CraftLine line = plan.lines[i];
                    if (line.HasWork || now < line.NextAcquireTick) continue;
                    probes--;
                    try
                    {
                        BillProbe.TryAcquire(this, plan, line, map, w, now);
                    }
                    catch (Exception e)
                    {
                        // 一条线出问题不该让整台建筑每 tick 刷红字（实机炸过一次：诊断里的活动区判定）
                        Log.ErrorOnce("[DigitalStorage] 制作代理取活异常（已跳过这条线，5 秒后重试）：" + e, 881133);
                        line.ClearWork();
                        line.BlockKey = "DS_BA_NoBill";
                        line.NextAcquireTick = now + 300;
                    }
                }
            }
        }

        /// <summary>资质载体（懒建）。造失败要退避 —— 否则会每 tick 跑一次 <c>PawnGenerator</c>。</summary>
        public Pawn Worker
        {
            get
            {
                if (worker != null && !worker.Destroyed) return worker;

                int now = Find.TickManager.TicksGame;
                if (now < nextWorkerAttemptTick) return null;
                nextWorkerAttemptTick = now + 250;
                worker = DigitalWorkerFactory.Create(Props.skillLevel, "数字制作工", "Craft" + Props.skillLevel);
                return worker;
            }
        }

        // ===================================================================
        // 维持数量（原版 TargetCount）
        // ===================================================================

        /// <summary>
        /// 维持数量模式用的**计数 bill**：挂在任意一张能做该配方的工作台上 ——
        /// 只为让 <c>Bill.Map</c> 有值（原版计数器要读 <c>bill.Map.resourceCounter</c> /
        /// <c>listerThings</c> / haul source 表）。它**不参与生产**，只是递给
        /// <c>recipe.WorkerCounter.CountProducts</c> 当参数。
        ///
        /// <para>为什么敢直接复用原版计数器：<c>RecipeWorkerCounter.CountProducts</c> 自己就遍历
        /// <c>AllHaulSourcesListForReading</c>（<c>RecipeWorkerCounter.cs:45-48</c>）⇒
        /// **产物进了核心容器也照样被数到**，不需要我们再写一套计数。</para>
        /// </summary>
        private Bill_Production CountingBill(CraftPlan plan, Map map)
        {
            if (plan.CountBill != null && !plan.CountBill.DeletedOrDereferenced) return plan.CountBill;
            if (plan == null || plan.recipe == null || map == null) return null;

            Thing bench = null;
            for (int i = 0; i < benches.Count; i++)
            {
                if (CanCraft(benches[i], plan.recipe)) { bench = benches[i]; break; }
            }
            IBillGiver giver = bench as IBillGiver;
            if (giver == null) return null;

            Bill_Production bill = new Bill_Production(plan.recipe);
            bill.repeatMode = BillRepeatModeDefOf.TargetCount;
            bill.targetCount = plan.targetCount;
            bill.pauseWhenSatisfied = plan.pauseWhenSatisfied;
            bill.unpauseWhenYouHave = plan.ResumeAt;

            BillStack stack = new BillStack(giver);
            stack.AddBill(bill);
            plan.CountBill = bill;
            plan.CountStack = stack;
            return bill;
        }

        /// <summary>
        /// 重数产物并更新"这一轮要不要开工"（30 tick 节流；<c>countedTick == 0</c> 表示强制重数）。
        /// 面板与生产线调度都调它，保证两边看到同一个数。
        /// </summary>
        internal void RefreshTargetStateIfStale(CraftPlan plan, Map map)
        {
            if (plan == null) return;
            if (plan.mode != CraftPlan.ModeTarget)
            {
                plan.wantsWork = true;
                return;
            }

            int now = Find.TickManager.TicksGame;
            if (plan.countedTick > 0 && now - plan.countedTick < 30) return;

            Bill_Production bill = CountingBill(plan, map);
            if (bill == null || bill.Map == null)
            {
                // 台子拆了 / 换图了 ⇒ 丢掉计数 bill（下次重建）。数不了就不开工：
                // 面板那边会显示"范围内没有可用的工作台"，比乱做一通好。
                plan.CountBill = null;
                plan.CountStack = null;
                plan.countedCount = 0;
                plan.countedTick = now;
                plan.wantsWork = false;
                return;
            }

            bill.targetCount = plan.targetCount;
            bill.pauseWhenSatisfied = plan.pauseWhenSatisfied;
            bill.unpauseWhenYouHave = plan.ResumeAt;

            int counted;
            try
            {
                counted = plan.recipe.WorkerCounter.CountProducts(bill);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 维持数量计数失败（本轮按 0 算）：" + e, 883355);
                counted = 0;
            }

            plan.countedCount = Math.Max(0, counted);
            plan.countedTick = now;
            plan.wantsWork = plan.UpdateTargetState(plan.countedCount);
        }

        /// <summary>这个配方能不能用"维持数量"（原版同一判据：单产物且无特殊产物）。</summary>
        internal bool CanTargetMode(CraftPlan plan)
        {
            if (plan == null || plan.recipe == null) return false;
            Bill_Production bill = (plan.CountBill != null && !plan.CountBill.DeletedOrDereferenced)
                ? plan.CountBill
                : CountingBill(plan, parent.Map);
            if (bill == null) return true;      // 没台子时无从判断，先允许（面板会提示没有可用工作台）
            try
            {
                return plan.recipe.WorkerCounter.CanCountProducts(bill);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>模式按钮：无限 → 次数(1) → 维持数量(10) → 无限（不可计数的配方跳过维持数量）。</summary>
        internal void CycleMode(CraftPlan plan)
        {
            if (plan == null) return;
            if (plan.mode == CraftPlan.ModeForever)
            {
                plan.mode = CraftPlan.ModeCount;
                plan.remaining = 0;
                plan.AddCount(1);
                return;
            }
            if (plan.mode == CraftPlan.ModeCount)
            {
                if (CanTargetMode(plan)) plan.SetTarget(plan.targetCount > 0 ? plan.targetCount : 10);
                else plan.SetForever();
                return;
            }
            plan.SetForever();
        }

        // ===================================================================
        // 面板操作（ITab 调用）
        // ===================================================================

        internal bool HasPlan(RecipeDef recipe)
        {
            for (int i = 0; i < plans.Count; i++)
            {
                if (plans[i].recipe == recipe) return true;
            }
            return false;
        }

        /// <summary>添加一条配方（默认无限模式）。已存在则不加。</summary>
        internal void AddPlan(RecipeDef recipe)
        {
            if (recipe == null || HasPlan(recipe)) return;
            CraftPlan plan = new CraftPlan();
            plan.recipe = recipe;
            plan.mode = CraftPlan.ModeForever;
            plans.Add(plan);
        }

        internal void RemovePlan(CraftPlan plan)
        {
            if (plan == null) return;
            for (int i = plan.lines.Count - 1; i >= 0; i--) ReleaseLine(plan, i);
            plans.Remove(plan);
        }

        /// <summary>上下移动（决定台子不够时谁先拿到线）。</summary>
        internal void MovePlan(CraftPlan plan, int dir)
        {
            int i = plans.IndexOf(plan);
            if (i < 0) return;
            int j = i + dir;
            if (j < 0 || j >= plans.Count) return;
            plans[i] = plans[j];
            plans[j] = plan;
        }

        // ===================================================================
        // 电力
        // ===================================================================

        public void RecalcWatts()
        {
            float w = Props.basePowerWatts;
            if (enabled) w += BenchWatts();
            cachedWatts = w * OverclockPowerMult;
            ReassertPower();
        }

        /// <summary>
        /// 把缓存值复读回 <c>CompPowerTrader</c>。原版会在若干时机调 <c>SetUpPowerVars</c>
        /// 把输出打回 <c>-Props.PowerConsumption</c>（电网变化 <c>PowerNetManager.cs:125/160</c>、
        /// 研究完成 <c>ResearchManager.cs:450</c>），所以不能只写一次。
        /// </summary>
        private void ReassertPower()
        {
            if (cachedWatts < 0f) return;
            CompPowerTrader p = parent.TryGetComp<CompPowerTrader>();
            if (p == null) return;
            float want = -cachedWatts;
            if (!Mathf.Approximately(p.PowerOutput, want)) p.PowerOutput = want;
        }

        // ===================================================================
        // 表现 + 左上角提示
        // ===================================================================

        private void UpdateVisuals(Map map)
        {
            int cap = Math.Max(0, Props.maxVisualSlots);
            int index = 0;
            for (int p = 0; p < plans.Count; p++)
            {
                CraftPlan plan = plans[p];
                for (int i = 0; i < plan.lines.Count; i++)
                {
                    plan.lines[i].TickVisual(map, index, cap);
                    index++;
                }
            }
        }

        /// <summary>记一笔产物（给左上角提示聚合）。<paramref name="stored"/> = 已进核心。</summary>
        internal void NoteProduct(Thing p, bool stored)
        {
            if (p == null) return;
            string key = BillCraftFunnel.DescribeProduct(p);
            int amount = Math.Max(1, p.stackCount);

            Dictionary<string, int> map = stored ? pendingStored : pendingDropped;
            int cur;
            map.TryGetValue(key, out cur);
            map[key] = cur + amount;

            if (stored) completedCount++;
            else droppedCount++;
        }

        /// <summary>某条配方的订单做完（次数模式清零）时提示一次。</summary>
        internal void NotifyPlanDone(CraftPlan plan)
        {
            if (plan == null || plan.recipe == null) return;
            Messages.Message("DS_BA_PlanDone".Translate(plan.recipe.LabelCap, plan.completed),
                new TargetInfo(parent.PositionHeld, parent.MapHeld), MessageTypeDefOf.TaskCompletion, false);
        }

        /// <summary>
        /// 按 <see cref="CompProperties_BillAutomation.messageIntervalTicks"/> 的节奏，把这段时间做好的产物
        /// 汇成**一条**左上角消息（用户拍板：产物直塞核心 + 左上角弹提示）。品质信在生成时被抑制，
        /// 品质信息用"传奇 XX"补在这里。
        /// </summary>
        private void FlushMessages(int now)
        {
            if (pendingStored.Count == 0 && pendingDropped.Count == 0) return;
            if (now - lastMessageTick < Math.Max(0, Props.messageIntervalTicks)) return;
            lastMessageTick = now;

            string stored = BuildList(pendingStored);
            string dropped = BuildList(pendingDropped);

            TargetInfo look = new TargetInfo(parent.PositionHeld, parent.MapHeld);
            if (stored != null)
            {
                Messages.Message("DS_BA_Done".Translate(stored), look, MessageTypeDefOf.TaskCompletion, false);
            }
            if (dropped != null)
            {
                Messages.Message("DS_BA_DoneDropped".Translate(dropped), look, MessageTypeDefOf.CautionInput, false);
            }

            pendingStored.Clear();
            pendingDropped.Clear();
        }

        private static string BuildList(Dictionary<string, int> map)
        {
            if (map.Count == 0) return null;
            StringBuilder sb = new StringBuilder();
            int shown = 0;
            foreach (KeyValuePair<string, int> kv in map)
            {
                if (shown >= 4)
                {
                    sb.Append("…");
                    break;
                }
                if (shown > 0) sb.Append("、");
                sb.Append(kv.Key);
                sb.Append(" ×");
                sb.Append(kv.Value);
                shown++;
            }
            return sb.ToString();
        }

        // ===================================================================
        // Gizmo / 检查面板 / 存档
        // ===================================================================

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (!parent.Spawned) yield break;

            yield return new Command_Toggle
            {
                icon = GizmoTex(ref texToggle, "UI/Gizmos/制作代理-开关"),
                defaultLabel = "DS_BA_Toggle".Translate(),
                defaultDesc = "DS_BA_ToggleDesc".Translate(),
                isActive = () => enabled,
                toggleAction = () => { Enabled = !enabled; }
            };

            yield return new Command_Action
            {
                icon = GizmoTex(ref texOverclock, "UI/Gizmos/制作代理-超频"),
                defaultLabel = "DS_BA_Overclock".Translate(OverclockLabel()),
                defaultDesc = "DS_BA_OverclockDesc".Translate(CurrentWatts.ToString("#####0")),
                action = () =>
                {
                    OverclockTier = (overclockTier + 1) % 4;
                    Messages.Message("DS_BA_OverclockMsg".Translate(parent.LabelShort, OverclockLabel(), CurrentWatts.ToString("#####0")),
                        new TargetInfo(parent.PositionHeld, parent.MapHeld), MessageTypeDefOf.SilentInput, false);
                }
            };

            // 显示扫描范围（用户要求）：开着就一直在地图上画 13×13 边框，方便摆工作台。
            yield return new Command_Toggle
            {
                icon = GizmoTex(ref texRange, "UI/Gizmos/制作代理-显示范围"),
                defaultLabel = "DS_BA_ShowRange".Translate(),
                defaultDesc = "DS_BA_ShowRangeDesc".Translate(Props.scanRadius * 2 + 1),
                isActive = () => showRange,
                toggleAction = () => { showRange = !showRange; }
            };
        }

        /// <summary>
        /// 三个 gizmo 的图标（源文件是 SVG，见 <c>Tools/svg2png/</c>）。
        ///
        /// <para>⚠️ <b>不能不给图标</b>：<c>Command.DrawIcon</c> 在 <c>icon == null</c> 时会画
        /// <c>BaseContent.BadTex</c>（原版那个"坏贴图"占位）—— 三个按钮会各顶一个占位图。</para>
        ///
        /// <para>按 comp 实例缓存：<c>CompGetGizmosExtra</c> 是**每帧**被调的（检视面板开着时），
        /// 虽然 <c>ContentFinder</c> 自己也有字典缓存，但这里省掉每帧三次字符串查表。
        /// 缓存字段用 Unity 的"假 null"判空 ⇒ 开发者模式重载贴图后会自动重取。</para>
        /// </summary>
        private Texture2D GizmoTex(ref Texture2D cache, string path)
        {
            if (cache == null) cache = ContentFinder<Texture2D>.Get(path, true);
            return cache;
        }

        /// <summary>当前超频档位名（面板/描述用）。</summary>
        public string OverclockLabel()
        {
            switch (overclockTier)
            {
                case 1: return "DS_BA_OC_3".Translate();
                case 2: return "DS_BA_OC_6".Translate();
                case 3: return "DS_BA_OC_9".Translate();
                default: return "DS_BA_OC_Off".Translate();
            }
        }

        /// <summary>
        /// 检查面板状态行（走 keyed，见 <c>Languages/*/Keyed</c> 的 <c>DS_BA_Inspect*</c>）。
        /// </summary>
        public override string CompInspectStringExtra()
        {
            if (!parent.Spawned) return null;
            if (!Powered) return "DS_BA_InspectOff".Translate().ToString();
            if (!enabled) return "DS_BA_InspectDisabled".Translate().ToString();

            int lines = TotalLines();
            string s = "DS_BA_InspectWork".Translate(lines, plans.Count, CurrentWatts.ToString("#####0"),
                Props.workSpeedMult.ToString("0.0"), OverclockSpeedMult.ToString("0"),
                Props.skillLevel).ToString();
            if (benches.Count == 0) s += "DS_BA_InspectNoBench".Translate().ToString();
            return s;
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            RecalcWatts();
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            if (slotsMap == null) slotsMap = map;
            ReleaseAll();
            slotsMap = null;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref enabled, "billAutoEnabled", true);
            Scribe_Values.Look(ref overclockTier, "billAutoOverclock", 0);
            Scribe_Values.Look(ref showRange, "billAutoShowRange", false);
            Scribe_Collections.Look(ref plans, "billAutoPlans", LookMode.Deep);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (plans == null) plans = new List<CraftPlan>();
                // 配方 def 被删（换 mod/换版本）时安静丢掉那条，别让面板里出现空行
                for (int i = plans.Count - 1; i >= 0; i--)
                {
                    if (plans[i] == null || plans[i].recipe == null) plans.RemoveAt(i);
                }
                cachedWatts = -1f;
                RecalcWatts();
            }
        }
    }
}
