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

        /// <summary>
        /// ★ 第 2 步（AE2 合成内核）：合成 Job 列表（**进存档**）= AE2 的那张 job 表。
        /// 一个 Job = 一次合成请求 + 它那棵依赖树；<see cref="plans"/> 依旧是执行层。
        /// </summary>
        private List<CraftJob> jobs = new List<CraftJob>();

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

        // =================================================================================
        // ★ U1 组（AE2 合成 CPU 语义）：
        //   · 合成存储 = 1k / 4k / 16k / 64k —— 一次合成请求最大能有多大，装不下就拒绝提交
        //     （AE2 的 CraftingCpuLogic.submitJob 返回 TOO_SMALL ⇒ 客户端显示 CPU_TOO_SMALL）
        //   · 并行处理单元 0..8 —— 同一个步骤最多同时占 1+单元数 张工作台（AE2 的 co-processor）
        //   两项都进存档；老存档没有这两个节点 ⇒ 用默认 4k / 4，行为与改之前完全一致。
        // =================================================================================

        /// <summary>合成存储档位：0 = 1k，1 = 4k，2 = 16k，3 = 64k。</summary>
        private int craftingStorageTier = 3;

        /// <summary>并行处理单元数量（0..8）：同一个步骤最多同时占 1 + 这个数 张工作台。</summary>
        private int parallelUnits = 8;

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
            int radius = ScanRadius;
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

            // ★ 第 2 步验证件：载入后自动跑一次内核自测，把结果写进 Player.log（每会话一次）
            KernelSelfTest.TickOnce(this, now);

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
                /// <summary>
        /// 通用配方兼容（新写）：把"玩家当前能做"的所有配方并入可做清单 ——
        /// 这样任何 mod 新增的配方（没有挂到任何工作台上）也能被制作代理接单。
        /// </summary>
        private void MergePlayerCraftableRecipes()
        {
            try
            {
                List<RecipeDef> all = DefDatabase<RecipeDef>.AllDefsListForReading;
                for (int i = 0; i < all.Count; i++)
                {
                    RecipeDef r = all[i];
                    if (r == null || r.ProducedThingDef == null) continue;      // 只收有产物的
                    if (r.researchPrerequisite != null && !r.researchPrerequisite.IsFinished) continue;   // 研究未解锁
                    if (r.recipeUsers == null || r.recipeUsers.Count == 0) continue;                       // 没有任何工作台（= 被隐藏的测试配方）
                    // 只在"范围内**已有**的工作台能做这条配方"时才并入（读到了制作点再加）
                    bool hasBench = false;
                    for (int k = 0; k < r.recipeUsers.Count && !hasBench; k++)
                    {
                        ThingDef u = r.recipeUsers[k];
                        if (u == null) continue;
                        for (int b = 0; b < benchDefs.Count; b++)
                        {
                            if (benchDefs.Contains(u)) { hasBench = true; break; }
                        }
                    }
                    if (!hasBench) continue;
                    // MergePlayerCraftableRecipesV2
                    if (unlocked.Contains(r)) continue;
                    unlocked.Add(r);
                }
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[DigitalStorage] 并入全部可用配方时出错（已忽略）：" + ex.Message, 889912);
            }
        }

private void RescanBenches(Map map)
        {
            if (slotsMap != null && slotsMap != map) ReleaseAll();
            slotsMap = map;

            benches.Clear();
            benchDefs.Clear();

            int radius = ScanRadius;
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
            MergePlayerCraftableRecipes();   // ★ 通用兼容：并入所有玩家可做的配方
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
            int now = Find.TickManager.TicksGame;

            // ★ 第 2 步：先刷新 Job 状态（根步骤达标 ⇒ Job 完成 + toast）。
            RefreshJobStates(map, now);

            // 台子够不够用：下面"缺料的步骤让位"只在**真的抢台子**时才做
            bool atLineCap = TotalLines() >= Math.Max(1, Props.maxSlots);

            for (int p = plans.Count - 1; p >= 0; p--)
            {
                CraftPlan plan = plans[p];
                for (int i = plan.lines.Count - 1; i >= 0; i--)
                {
                    CraftLine line = plan.lines[i];
                    Thing b = line.Bench;

                    // ★ 第 2 步（AE2：加工位是**临时占用**的 —— 材料不齐的步骤根本推不下去）：
                    //   空闲产线在两种情况下让位（**正在做的那一件照做**：HasWork 时一律不动）：
                    //     ① 这一轮不需要开工了（维持达标 / 挂起 / 做完）——"到位即还台子"；
                    //     ② 因为**缺料**被挡住超过 2.5 秒，而且台子已经不够用 ——"等料不占位"。
                    if (!line.HasWork && line.Bill != null)
                    {
                        if (IsStarvedForMaterials(line))
                        {
                            if (line.BlockedSinceTick <= 0) line.BlockedSinceTick = now;
                        }
                        else
                        {
                            line.BlockedSinceTick = 0;
                        }

                        bool yieldForDone = !plan.CanStartNewWork;
                        bool yieldForStarved = atLineCap && line.BlockedSinceTick > 0
                            && now - line.BlockedSinceTick >= 150;
                        if (yieldForDone || yieldForStarved)
                        {
                            ReleaseLine(plan, i);
                            continue;
                        }
                    }
                    else if (line.BlockedSinceTick != 0)
                    {
                        line.BlockedSinceTick = 0;
                    }

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

            // ★ 第 2 步：分配顺序 = **上游优先**（中间产物先拿台子，根最后）——
            //   AE2 里这一步是自然涌现的（拿不到料就跳过），DS 必须显式排。
            List<CraftPlan> order = JobOrderedPlans();

            int free = Math.Max(1, Props.maxSlots) - TotalLines();
            for (int p = 0; p < order.Count && free > 0; p++)
            {
                CraftPlan plan = order[p];
                if (!plan.Maintained) continue;

                // 维持数量模式：先按原版计数器数一遍，决定这一轮要不要开新活
                //（达标 ⇒ wantsWork=false ⇒ 不再配新线；**在产的那几件照做**，与原版 paused 语义一致）
                RefreshTargetStateIfStale(plan, map);
                if (!plan.CanStartNewWork) continue;

                // ★ U1 组：并行处理单元 = 同一个步骤最多同时占几张台子（AE2 的 co-processor）。
                //   默认 4 ⇒ 每步最多 5 张，比"一个步骤吃掉范围内所有空闲台子"温和，也给了可调余地。
                int want = Mathf.Min(FreeBenchCountFor(plan.recipe), Mathf.Max(1, ParallelPerStep));
                for (int i = plan.lines.Count; i < want && free > 0; i++)
                {
                    Thing bench = PickBench(plan.recipe);
                    if (bench == null) break;

                    if (!BillBenchClaims.TryClaim(map, bench, this))
                    {
                        usedBenches.Add(bench);   // 别人占了 ⇒ 本轮别再挑它
                        continue;
                    }
                    usedBenches.Add(bench);
                    plan.lines.Add(MakeLine(plan, bench));
                    free--;
                }
            }
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

        /// <summary>还能再给这个配方配几条线（可用 + 未被占用 + 认领得动）。</summary>
        private int FreeBenchCountFor(RecipeDef recipe)
        {
            int n = 0;
            for (int i = 0; i < benches.Count; i++)
            {
                Thing b = benches[i];
                if (!CanCraft(b, recipe) || !BenchUsable(b)) continue;
                if (usedBenches.Contains(b)) continue;
                n++;
            }
            return n;
        }

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
                worker = DigitalWorkerFactory.Create(Props.skillLevel, "制作代理", "代理");
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
        // ★ 第 2 步：合成 Job（AE2 的 CraftingJob / CraftingCPUCluster）
        //   一个请求 = 一棵依赖树；取消 = 整树一起销毁；分配台子 = 上游优先。
        //   这里**不碰**执行层（plans / lines / 结算链），只加"归属 + 依赖 + 取消传播"。
        // ===================================================================

        /// <summary>给界面看的 Job 列表（只读视图，与 <see cref="PlansForReading"/> 同规矩）。</summary>
        internal IList<CraftJob> JobsForReading
        {
            get
            {
                // 读档路径下 jobs 可能是 null（原版 Scribe_Collections.Look 在"存档里没有这个节点"时会置空）
                if (jobs == null) jobs = new List<CraftJob>();
                // ★ U1（用户定案）：合成存储 / 并行处理单元**不给选择、一律最高** ——
                //   老存档里可能存着旧档位（1/4），读档时直接拉满，保证行为统一。
                craftingStorageTier = 3;   // 64k
                parallelUnits = 8;         // 每步最多 9 张台子
                return jobs;
            }
        }

        // ---- ★ U1 组：合成 CPU 的能力（AE2 的"合成存储 / 并行处理单元"）----

        /// <summary>合成存储的档位名（1k / 4k / 16k / 64k），界面与提示里显示这个。</summary>
        public string StorageTierLabel()
        {
            switch (craftingStorageTier)
            {
                case 0: return "1k";
                case 2: return "16k";
                case 3: return "64k";
                default: return "4k";
            }
        }

        /// <summary>合成存储的容量（单位 = 件；照 AE2 用 2 的幂）。</summary>
        public int StorageBytes
        {
            get
            {
                switch (craftingStorageTier)
                {
                    case 0: return 1024;
                    case 2: return 16384;
                    case 3: return 65536;
                    default: return 4096;
                }
            }
        }

        /// <summary>并行处理单元数量（界面显示用）。</summary>
        public int ParallelUnits
        {
            get { return Mathf.Clamp(parallelUnits, 0, 8); }
        }

        /// <summary>同一个步骤最多同时占几张工作台（1 + 并行单元数）。</summary>
        public int ParallelPerStep
        {
            get { return 1 + ParallelUnits; }
        }

        /// <summary>合成存储档位循环：1k → 4k → 16k → 64k → 1k。</summary>
        public void CycleStorageTier()
        {
            craftingStorageTier = (craftingStorageTier + 1) % 4;
        }

        /// <summary>并行处理单元 +1（到 8 回 0）。</summary>
        public void CycleParallelUnits()
        {
            parallelUnits = (ParallelUnits + 1) % 9;
        }

        /// <summary>一个已建好的 Job 的"计划规模"（件）= 各步骤需要量之和，用于容量条与占用统计。</summary>
        internal static int PlanBytesOf(CraftJob job)
        {
            if (job == null || job.steps == null) return 0;
            int n = 0;
            for (int i = 0; i < job.steps.Count; i++)
            {
                CraftJobStep s = job.steps[i];
                if (s != null) n += Mathf.Max(0, s.needCount);
            }
            return n;
        }

        /// <summary>所有在跑的 Job 占用的合成存储（件）。</summary>
        public int StorageUsed
        {
            get
            {
                int n = 0;
                IList<CraftJob> list = JobsForReading;
                for (int i = 0; i < list.Count; i++)
                {
                    CraftJob job = list[i];
                    if (job != null && job.Active) n += PlanBytesOf(job);
                }
                return n;
            }
        }

        /// <summary>这个配方被哪个**还在跑**的 Job 需要（null = 没有）。</summary>
        internal CraftJob JobForRecipe(RecipeDef recipe)
        {
            if (recipe == null) return null;
            for (int i = 0; i < jobs.Count; i++)
            {
                CraftJob job = jobs[i];
                if (job == null || !job.Active) continue;
                if (job.IndexOfRecipe(recipe) >= 0) return job;
            }
            return null;
        }

        /// <summary>
        /// <b>提交一个合成请求</b>（AE2 的 <c>ICraftingService.submitJob</c>）：
        /// <c>CraftTree.Build</c> 算出依赖树（AE2 的 CraftingCalculation）⇒ 逐个节点确保有一条订单
        /// ⇒ 记成 Job 的 steps。**先建上游、最后建根**：DS 的台子按列表顺序先到先得，
        /// 根排前面会先把台子占光、然后卡在"等中间产物"（AF 组修过一次的同一个坑，这里是根治）。
        /// </summary>
        internal CraftJob SubmitJob(RecipeDef recipe, ThingDef product, int wanted, bool withIntermediates)
        {
            if (recipe == null) return null;

            int want = Mathf.Max(1, wanted);
            if (product == null) product = recipe.ProducedThingDef;

            // ★ U1 组（AE2：合成 CPU 的合成存储装不下这个计划 ⇒ CPU_TOO_SMALL，**拒绝提交**）：
            //   计划规模 = 树里所有件数之和（≈ AE2 的 bytes）。超了就说清"需要多大 / 现在多大"，
            //   而不是默默收下然后永远做不完。
            int planBytes = PlanBytesForSubmit(recipe, product, want, withIntermediates);
            if (planBytes > StorageBytes)
            {
                Messages.Message("DS_JOB_St_TooSmall".Translate(
                        recipe.LabelCap, planBytes.ToString("N0"), StorageTierLabel()),
                    new TargetInfo(parent.PositionHeld, parent.MapHeld), MessageTypeDefOf.RejectInput, false);
                return null;
            }

            CraftJob job = new CraftJob();
            job.rootRecipe = recipe;
            job.rootProduct = product;
            job.wanted = want;
            job.createdTick = Find.TickManager.TicksGame;
            job.state = CraftJobState.Crafting;

            CraftJobStep root = new CraftJobStep();
            root.recipe = recipe;
            root.product = product;
            root.needCount = want;
            root.crafts = Mathf.Max(1, Mathf.CeilToInt((float)want / Mathf.Max(1, CraftTree.YieldOf(recipe, product))));
            root.depth = 0;
            root.parent = -1;
            job.steps.Add(root);

            List<CraftJobStep> upstream = new List<CraftJobStep>();
            if (withIntermediates && product != null)
            {
                CraftTreeNode tree = CraftTree.Build(this, recipe, product, want);
                CollectJobSteps(job, 0, tree, upstream);
                upstream.Sort(delegate (CraftJobStep a, CraftJobStep b) { return b.depth - a.depth; });
            }

            // ★ 上游也要**每次重算并覆盖**数量：用户再发一次请求时，上游需求必须跟着变（实机反馈）
            for (int i = 0; i < upstream.Count; i++) EnsureStepPlan(upstream[i], false, true);
            EnsureStepPlan(root, true, true);

            JobsForReading.Add(job);
            return job;
        }

        /// <summary>
        /// ★ U1 组：这次提交的"计划规模"（件）= 根 + 所有中间产物的需要量之和。
        /// 与 <see cref="PlanBytesOf"/> 同一口径（那边算的是已经建好的 Job）。
        /// "不连中间产物"时就只有根的量。
        /// </summary>
        private int PlanBytesForSubmit(RecipeDef recipe, ThingDef product, int want, bool withIntermediates)
        {
            if (recipe == null) return 0;
            int n = Mathf.Max(1, want);
            if (!withIntermediates || product == null) return n;
            try
            {
                CraftTreeNode tree = CraftTree.Build(this, recipe, product, Mathf.Max(1, want));
                List<CraftTreePlanRow> rows = CraftTree.FlattenIntermediate(tree);
                for (int i = 0; i < rows.Count; i++)
                {
                    if (rows[i] != null) n += Mathf.Max(0, rows[i].Total);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 估算合成规模失败（按根的量算）：" + e, 664422);
            }
            return n;
        }

        /// <summary>把合成树的中间产物收集成 steps（菱形依赖复用同一个 step，取最深的深度）。</summary>
        private void CollectJobSteps(CraftJob job, int parentIndex, CraftTreeNode node, List<CraftJobStep> upstream)
        {
            if (job == null || node == null || node.Children == null) return;
            for (int i = 0; i < node.Children.Count; i++)
            {
                CraftTreeNode child = node.Children[i];
                if (child == null || child.Recipe == null || child.Product == null) continue;
                if (child.CoveredByCore || child.NoRecipe) continue;   // 核心里够了 / 没有配方 ⇒ 不是"要代工的步骤"

                int idx = job.IndexOfRecipe(child.Recipe);
                int myIndex;
                if (idx >= 0)
                {
                    CraftJobStep exist = job.steps[idx];
                    if (child.Depth > exist.depth) exist.depth = child.Depth;
                    myIndex = idx;
                }
                else
                {
                    CraftJobStep step = new CraftJobStep();
                    step.recipe = child.Recipe;
                    step.product = child.Product;
                    step.needCount = Mathf.Max(1, child.RequiredTotal);
                    step.crafts = Mathf.Max(1, child.Crafts);
                    step.depth = child.Depth;
                    step.parent = (parentIndex >= 0 && parentIndex < job.steps.Count) ? parentIndex : -1;
                    job.steps.Add(step);
                    myIndex = job.steps.Count - 1;
                    if (step.parent >= 0) job.steps[step.parent].children.Add(myIndex);
                    upstream.Add(step);
                }
                CollectJobSteps(job, myIndex, child, upstream);
            }
        }

        /// <summary>确保这个步骤有一条订单；overwriteTarget = 要不要覆盖已有订单的目标（根要，中间产物只在新建时设）。</summary>
        private CraftPlan EnsureStepPlan(CraftJobStep step, bool isRoot, bool overwrite)
        {
            if (step == null || step.recipe == null) return null;
            CraftPlan plan = CraftTree.FindPlan(this, step.recipe);
            bool fresh = (plan == null);
            if (fresh)
            {
                AddPlan(step.recipe);
                plan = CraftTree.FindPlan(this, step.recipe);
                if (plan != null) plan.FromJob = true;   // 只有 Job 建出来的订单才参与"取消剪枝"
            }
            if (plan != null && (fresh || overwrite))
            {
                // ★ 用户定案的依赖树语义（这才是对的）：
                //   · **根**（overwriteTarget=true）= 维持 N + 达标即暂停：保持库里有 N 件，被用掉自动补做；
                //   · **上游各步** = 次数模式：按根的需求量**精确做这么多件就停**，绝不因为被下游吃掉而补做
                //     （原来上游也用"维持" ⇒ 互相吃掉又补做，实机出现过 21/9/11 这种暴涨）。
                int need = Mathf.Max(1, step.needCount);
                // ★ 上游用**次数模式**（做满精确件数就停）：实测「维持+达标即暂停」在上游会立刻被判成
                //   已达标（0/2 也显示已达标）⇒ 根本不开工。根订单仍保持「维持N+达标即暂停」。
                if (isRoot)
                {
                    plan.SetTarget(need);
                    plan.pauseWhenSatisfied = true;
                }
                else
                {
                    plan.mode = CraftPlan.ModeCount;
                    plan.remaining = need;
                    plan.pauseWhenSatisfied = false;
                }
                plan.paused = false;
                plan.suspended = false;
            }
            return plan;
        }

        /// <summary>
        /// <b>取消整个合成</b>（AE2 的 <c>CraftingCpuLogic.cancel()</c> ⇒ <c>finishJob(false)</c> ⇒ <c>job = null</c>）：
        /// 拿掉 Job，再剪掉"**只被这个 Job 需要**、且是 Job 建出来的"那些订单 —— 别的 Job 还要用的中间产物不会误删。
        /// 材料不退（AE2 也不退）：已经做出来的中间产物留在核心。
        /// </summary>
        internal void CancelJob(CraftJob job)
        {
            if (job == null) return;
            job.state = CraftJobState.Cancelled;
            job.finishedTick = Find.TickManager.TicksGame;
            if (jobs != null) jobs.Remove(job);

            int removed = PruneOrphanPlans();
            Messages.Message("DS_JOB_Cancelled".Translate(
                    (job.rootRecipe == null) ? "?" : job.rootRecipe.LabelCap.ToString(),
                    job.steps.Count, removed),
                new TargetInfo(parent.PositionHeld, parent.MapHeld), MessageTypeDefOf.NeutralEvent, false);
        }

        /// <summary>
        /// ★ 实机修 bug：剪枝要问"**任何** Job（含已完成的）还需不需要这条订单"。
        /// 原来只问 JobForRecipe（只看还在跑的 Job）⇒ 已完成的 Job 的订单会被当成孤儿删掉：
        /// 实机表现就是"取消一个请求，顺手把你另一条已做完的订单也删了"（自测报 取消=FAIL 残留 -1 条）。
        /// </summary>
        private bool AnyJobHasRecipe(RecipeDef recipe)
        {
            if (recipe == null || jobs == null) return false;
            for (int i = 0; i < jobs.Count; i++)
            {
                CraftJob job = jobs[i];
                if (job == null) continue;
                if (job.IndexOfRecipe(recipe) >= 0) return true;
            }
            return false;
        }

        /// <summary>剪掉"没有任何 Job 还需要"的、且是 Job 建出来的订单（= 取消整树的落地点）。</summary>
        private int PruneOrphanPlans()
        {
            int removed = 0;
            for (int i = plans.Count - 1; i >= 0; i--)
            {
                CraftPlan plan = plans[i];
                if (plan == null || plan.recipe == null) continue;
                if (!plan.FromJob) continue;                        // 玩家自己加的订单不动
                if (AnyJobHasRecipe(plan.recipe)) continue;          // ★ 任何 Job（含已完成）还需要它就不能删
                for (int k = plan.lines.Count - 1; k >= 0; k--) ReleaseLine(plan, k);
                plans.RemoveAt(i);
                removed++;
            }
            return removed;
        }

        /// <summary>★ 任何 Job（**含已完成的**）是否还需要这个配方 —— 删订单/剪枝都要问它，
        /// 只问"在跑的 Job"会导致：请求做完后删根不带走上游、剪枝误删已完成订单（实机两处 bug）。</summary>
        internal CraftJob AnyJobForRecipe(RecipeDef recipe)
        {
            if (recipe == null || jobs == null) return null;
            for (int i = 0; i < jobs.Count; i++)
            {
                CraftJob job = jobs[i];
                if (job == null) continue;
                if (job.IndexOfRecipe(recipe) >= 0) return job;
            }
            return null;
        }

        /// <summary>★ 这条配方是不是某个请求的**上游附属步骤**（不是根）—— 附属步骤由最终产物统管，
        /// 界面上不给单独的删除按钮，也不允许手动增删（用户要求：前面的订单都是最终产物的附属）。</summary>
        internal bool IsSubordinateStep(RecipeDef recipe)
        {
            if (recipe == null || jobs == null) return false;
            for (int i = 0; i < jobs.Count; i++)
            {
                CraftJob job = jobs[i];
                if (job == null || job.steps.Count == 0) continue;
                for (int k = 1; k < job.steps.Count; k++)   // 从 1 开始 = 跳过根
                {
                    CraftJobStep s = job.steps[k];
                    if (s != null && s.recipe == recipe) return true;
                }
            }
            return false;
        }

        /// <summary>刷新 Job 状态：根步骤达标 ⇒ Finished + toast（AE2 的 FinishedJobToast）。完成判据只看根。</summary>
        private void RefreshJobStates(Map map, int now)
        {
            if (jobs == null) jobs = new List<CraftJob>();
            for (int i = jobs.Count - 1; i >= 0; i--)
            {
                CraftJob job = jobs[i];
                if (job == null || job.rootRecipe == null) { jobs.RemoveAt(i); continue; }
                if (!job.Active)
                {
                    // 完成的 Job 留一会儿给玩家看，但别无限堆积（≈2 游戏小时后退场）
                    if (job.state == CraftJobState.Finished && job.finishedTick > 0
                        && now - job.finishedTick > 5000) jobs.RemoveAt(i);
                    continue;
                }

                CraftPlan rootPlan = CraftTree.FindPlan(this, job.rootRecipe);
                if (rootPlan == null) { jobs.RemoveAt(i); continue; }

                RefreshTargetStateIfStale(rootPlan, map);
                if (!StepSatisfied(rootPlan)) continue;

                // ★ 用户定案：最终产物达标 ⇒ 整棵树收工！过程里各步用维持（被拿走自动补做），
                //   但根一达标就：① 发完成提示；② 结束这个请求；③ 剪掉它的全部步骤订单（不再维持）。
                //   这正是 AE2：Job 完成 ⇒ CPU 释放 ⇒ 任务清空。
                Messages.Message("DS_JOB_Done".Translate(job.rootRecipe.LabelCap, job.wanted, job.steps.Count),
                    new TargetInfo(parent.PositionHeld, parent.MapHeld), MessageTypeDefOf.TaskCompletion, false);
                job.state = CraftJobState.Finished;
                job.finishedTick = now;
                jobs.RemoveAt(i);
                PruneOrphanPlans();
                continue;
            }
        }

        /// <summary>一个步骤算不算做完：**不自己记账**，读那条订单的现成状态。无限模式 = 没有"做完"这回事。</summary>
        internal static bool StepSatisfied(CraftPlan plan)
        {
            if (plan == null) return true;
            if (plan.mode == CraftPlan.ModeTarget) return !plan.wantsWork;
            if (plan.mode == CraftPlan.ModeCount) return plan.Done;
            return false;
        }

        /// <summary>是不是在"等料"（AE2 里 extractPatternInputs 返回 null 的那种状态）。只有这类阻塞才让出台子。</summary>
        private static bool IsStarvedForMaterials(CraftLine line)
        {
            if (line == null) return false;
            string k = line.BlockKey;
            return k == "DS_BA_Block_Material" || k == "DS_BA_NoCoreMaterial";
        }

        /// <summary>分配台子的顺序：**上游优先**（同深度沿用玩家自己的排序）。</summary>
        private List<CraftPlan> JobOrderedPlans()
        {
            List<CraftPlan> order = new List<CraftPlan>(plans);
            if (jobs == null || jobs.Count == 0 || plans.Count < 2) return order;

            Dictionary<RecipeDef, int> depth = new Dictionary<RecipeDef, int>();
            for (int i = 0; i < jobs.Count; i++)
            {
                CraftJob job = jobs[i];
                if (job == null) continue;
                for (int k = 0; k < job.steps.Count; k++)
                {
                    CraftJobStep s = job.steps[k];
                    if (s == null || s.recipe == null) continue;
                    int cur;
                    depth.TryGetValue(s.recipe, out cur);
                    if (s.depth > cur) depth[s.recipe] = s.depth;
                }
            }
            if (depth.Count == 0) return order;

            Dictionary<CraftPlan, int> index = new Dictionary<CraftPlan, int>();
            for (int i = 0; i < plans.Count; i++) index[plans[i]] = i;

            order.Sort(delegate (CraftPlan a, CraftPlan b)
            {
                int da = DepthOfRecipe(depth, a);
                int db = DepthOfRecipe(depth, b);
                if (da != db) return db - da;          // 越上游（depth 越大）越先拿台子
                int ia, ib;
                index.TryGetValue(a, out ia);
                index.TryGetValue(b, out ib);
                return ia - ib;
            });
            return order;
        }

        private static int DepthOfRecipe(Dictionary<RecipeDef, int> depth, CraftPlan plan)
        {
            if (plan == null || plan.recipe == null) return 0;
            int d;
            return depth.TryGetValue(plan.recipe, out d) ? d : 0;
        }

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

            // ★ 第 2 步（AE2 里没有"只删子树"这回事）：删掉的如果是某个合成 Job 的步骤，
            //   就按"取消整个合成"处理 —— **整棵树一起删**（用户实机报的"删了最终产物、下游订单不取消"的正解）。
            // ★ 用户要求：**上游附属步骤不能单独删**（由最终产物统管）——一律拒绝并提示。
            if (plan.recipe != null && IsSubordinateStep(plan.recipe))
            {
                Messages.Message("DS_JOB_SubCannotDelete".Translate(plan.recipe.LabelCap),
                    new TargetInfo(parent.PositionHeld, parent.MapHeld), MessageTypeDefOf.RejectInput, false);
                return;
            }

            CraftJob owner = (plan.recipe == null) ? null : AnyJobForRecipe(plan.recipe);   // ★ 含**已完成**的 Job：否则请求完成后删根不会带走上游
            if (owner != null)
            {
                CancelJob(owner);
                return;
            }

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

            // 范围档位（新写）：点一下轮换，显示当前范围
            yield return new Command_Action
            {
                icon = GizmoTex(ref texRange, "UI/Gizmos/制作代理-显示范围"),
                defaultLabel = "范围 " + RangeLabel(),
                defaultDesc = "点一下切换扫描范围（默认 → 更大 → 全图 → 回默认）。范围越大，能扫到的工作台与核心越多。",
                action = () => { CycleRange(); }
            };

            // ★ 第 2 步验证件：自测按钮（点一下把内核自测结果写进 Player.log，测试订单自动撤销）
            yield return new Command_Action
            {
                icon = GizmoTex(ref texRange, "UI/Gizmos/制作代理-显示范围"),
                defaultLabel = "DS_SELFTEST".Translate(),
                defaultDesc = "DS_SELFTEST_DESC".Translate(),
                action = () =>
                {
                    Log.Warning(KernelSelfTest.Run(this));
                    Messages.Message("DS_SELFTEST_DONE".Translate(),
                        new TargetInfo(parent.PositionHeld, parent.MapHeld), MessageTypeDefOf.SilentInput, false);
                }
            };

            // ★ AE2 界面样板（P1 骨架）：先把"脸"立起来，接数据放下一步
            yield return new Command_Action
            {
                icon = GizmoTex(ref texRange, "UI/Gizmos/制作代理-显示范围"),
                defaultLabel = "DS_AE2PREVIEW".Translate(),
                defaultDesc = "DS_AE2PREVIEW_DESC".Translate(),
                action = () => { Find.WindowStack.Add(new DigitalStorage.UI.Window_AE2CraftPanel(this)); }
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

                // ---- 扫描范围可调（新写）----
        private const int RangeWholeMap = -1;   // 全图
        private const int RangeDefault = 0;     // 用 Def 的 scanRadius
        private int rangeOverride = RangeDefault;

        /// <summary>本建筑实际生效的扫描半径（格子）。</summary>
        internal int ScanRadius
        {
            get
            {
                if (rangeOverride == RangeWholeMap)
                {
                    Map m = (parent != null) ? parent.Map : null;
                    return (m != null) ? Math.Max(m.Size.x, m.Size.z) : 200;
                }
                return (rangeOverride > 0) ? rangeOverride : Math.Max(0, Props.scanRadius);
            }
        }

        /// <summary>范围文字：全图 / N×N。</summary>
        internal string RangeLabel()
        {
            if (rangeOverride == RangeWholeMap) return "全图";
            int r = ScanRadius;
            return (r * 2 + 1) + "x" + (r * 2 + 1);
        }

        /// <summary>档位轮换：默认 → +0/4/9/17 → 全图 → 默认。</summary>
        internal void CycleRange()
        {
            int d = Math.Max(0, Props.scanRadius);
            int[] seq = new int[] { RangeDefault, d, d + 4, d + 9, d + 17, RangeWholeMap };
            int at = 0;
            for (int i = 0; i < seq.Length; i++) { if (seq[i] == rangeOverride) { at = i; break; } }
            rangeOverride = seq[(at + 1) % seq.Length];
        }

        internal void SetRange(int r) { rangeOverride = (r == RangeWholeMap) ? RangeWholeMap : Math.Max(0, Math.Min(60, r)); }
public override void PostExposeData()
        {
            Scribe_Values.Look(ref rangeOverride, "dsRangeOverride", RangeDefault);
            base.PostExposeData();
            Scribe_Values.Look(ref enabled, "billAutoEnabled", true);
            Scribe_Values.Look(ref overclockTier, "billAutoOverclock", 0);
            Scribe_Values.Look(ref showRange, "billAutoShowRange", false);
            // ★ U1 组：合成 CPU 的两项能力（老存档读不到 = 4k / 4 ⇒ 行为不变）
            Scribe_Values.Look(ref craftingStorageTier, "billAutoStorageTier", 3);
            Scribe_Values.Look(ref parallelUnits, "billAutoParallelUnits", 8);
            Scribe_Collections.Look(ref plans, "billAutoPlans", LookMode.Deep);
            // ★ 第 2 步：合成 Job（= 请求 + 依赖树）。读不到就是空表 ⇒ 老存档那批订单是"无主订单"，照旧能跑能删。
            Scribe_Collections.Look(ref jobs, "billAutoJobs", LookMode.Deep);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (plans == null) plans = new List<CraftPlan>();
                // ★ 这一行必须有：原版 Scribe_Collections.Look 在"存档里没有这个节点"时会把列表置成 null，
                //   而老存档正是这种情况 ⇒ 漏了它就是"读老存档后制作代理每 tick 空引用"（实机炸过一次）。
                if (jobs == null) jobs = new List<CraftJob>();
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
