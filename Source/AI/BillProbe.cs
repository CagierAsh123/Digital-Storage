using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// <b>取活</b>：把"我们自己那条 bill"临时换进工作台，问原版要原料。
    ///
    /// <para><b>为什么要换</b>：用户拍板"全程在面板操作、不需要操作工作台" ⇒ 台子上不会再有 bill。
    /// 而原版 <c>WorkGiver_DoBill.JobOnThing</c>（公开入口）只认台子 <c>BillStack</c> 里已有的 bill。
    /// 于是把我们的 bill 换进去一瞬（<see cref="BillStackSwap"/>），拿到它亲手挑好的原料后立刻还原 ——
    /// 营养换算、混料规则、stuff 取整、核心容器候选、地面候选全都不用重写。</para>
    ///
    /// <para><b>成本</b>：原版候选来源第一处就是本图 haul source（我们的核心，
    /// <c>WorkGiver_DoBill.cs:481-496</c>），且**在区域遍历之前**（<c>:525-528</c>）就返回 ⇒
    /// 核心里有料时只扫一遍容器内容物。核心里没料才会做全图区域遍历，那种情况用退避压住。
    /// 另外台子上**本来就不该有东西**（<c>IngredientStackCells = GenAdj.CellsOccupiedBy(this)</c>，
    /// 即台子自己占的格子），所以原版那句"先搬走台上的东西"不会误伤我们。</para>
    /// </summary>
    internal static class BillProbe
    {
        /// <summary>取活失败（没料/条件不满足）后的重试间隔。</summary>
        private const int RetryTicks = 30;

        /// <summary>
        /// "核心里凑不齐、但地面上有" / "这台子换不了 BillStack" 的退避（2 秒）。
        ///
        /// <para>⚠️ 刻意**不写** <c>bill.nextTickToSearchForIngredients</c>：那是原版给真人用的负缓存，
        /// 而且我们的 bill 是临时的，写它也没意义。</para>
        /// </summary>
        private const int CraftBackoffTicks = 120;

        private static List<WorkGiverDef> doBillDefs;

        /// <summary>所有 <c>giverClass</c> 派生自 <see cref="WorkGiver_DoBill"/> 的 WorkGiverDef（按 defName 稳定排序）。</summary>
        private static List<WorkGiverDef> DoBillDefs()
        {
            if (doBillDefs != null) return doBillDefs;
            List<WorkGiverDef> list = new List<WorkGiverDef>();
            List<WorkGiverDef> all = DefDatabase<WorkGiverDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                WorkGiverDef d = all[i];
                if (d == null || d.giverClass == null) continue;
                if (!typeof(WorkGiver_DoBill).IsAssignableFrom(d.giverClass)) continue;
                list.Add(d);
            }
            list.Sort((a, b) => string.CompareOrdinal(a.defName, b.defName));
            doBillDefs = list;
            return doBillDefs;
        }

        /// <summary>给一条在产线取一件活。返回 true 表示 <c>Ingredients/WorkLeft</c> 已就绪。</summary>
        public static bool TryAcquire(CompBillAutomation comp, CraftPlan plan, CraftLine line, Map map, Pawn w, int now)
        {
            line.ClearWork();
            line.BlockKey = "DS_BA_NoBill";

            if (plan == null || plan.recipe == null || !plan.Maintained) return false;

            Thing bench = line.Bench;
            if (bench == null || bench.Destroyed || !bench.Spawned) return false;

            IBillGiver giver = bench as IBillGiver;
            if (giver == null) return false;

            // 断电 / 缺燃料 / 故障 ⇒ 停（用户拍板：代理不代劳加燃料）
            if (!giver.CurrentlyUsableForBills()) { line.BlockKey = "DS_BA_BenchOff"; line.NextAcquireTick = now + RetryTicks; return false; }
            if (bench.IsBurning()) { line.BlockKey = "DS_BA_BenchOff"; line.NextAcquireTick = now + RetryTicks; return false; }

            // 真人正在用这台 ⇒ 让（等价于原版 JobOnThing 里那句 pawn.CanReserve(台子)，但不需要借地图）
            if (map.reservationManager != null
                && map.reservationManager.IsReservedByAnyoneOf(bench, Faction.OfPlayer))
            {
                line.BlockKey = "DS_BA_BenchBusy";
                line.NextAcquireTick = now + RetryTicks;
                return false;
            }

            if (line.Bill == null || line.TempStack == null)
            {
                line.BlockKey = "DS_BA_Block_Unsupported";
                line.NextAcquireTick = now + CraftBackoffTicks;
                return false;
            }

            List<WorkGiverDef> defs = DoBillDefs();
            if (defs.Count == 0) return false;

            Job got = null;
            BillStack original = null;
            bool swapped = false;
            DigitalWorkerScope.Enter(w, map, bench.PositionHeld);
            try
            {
                if (!BillStackSwap.TrySwapIn(bench, line.TempStack, out original))
                {
                    line.BlockKey = "DS_BA_Block_Unsupported";
                    line.NextAcquireTick = now + CraftBackoffTicks;
                    return false;      // finally 会负责退出作用域
                }
                swapped = true;

                for (int i = 0; i < defs.Count; i++)
                {
                    WorkGiverDef d = defs[i];
                    if (d.fixedBillGiverDefs != null && !d.fixedBillGiverDefs.Contains(bench.def)) continue;

                    WorkGiver_DoBill doBill;
                    try { doBill = d.Worker as WorkGiver_DoBill; }
                    catch (Exception) { continue; }
                    if (doBill == null) continue;

                    Job candidate;
                    try { candidate = doBill.JobOnThing(w, bench, false); }
                    catch (Exception e)
                    {
                        Log.ErrorOnce("[DigitalStorage] 制作代理取活失败（跳过这个 WorkGiver）："
                            + d.defName + " :: " + e, d.shortHash * 31 + 991);
                        continue;
                    }
                    if (candidate == null || candidate.bill == null) continue;
                    if (candidate.bill != line.Bill)
                    {
                        // 临时栈里只该有我们这一条 bill；出现别的说明有东西改了台子，安全起见放弃
                        continue;
                    }
                    got = candidate;
                    break;
                }

                if (got == null)
                {
                    // ⚠️ 诊断必须在**作用域内**做：里面的 cell.IsForbidden(pawn) 会走
                    // Pawn_PlayerSettings.EffectiveAreaRestrictionInPawnCurrentMap，
                    // 而它拿 pawn.MapHeld 当字典键 —— 假 pawn 一旦出了作用域，MapHeld 是 null
                    // ⇒ Dictionary.TryGetValue(null) 抛 ArgumentNullException（2026-10-03 实机炸过）。
                    line.BlockKey = Diagnose(bench, plan, map, w);
                    line.NextAcquireTick = now + RetryTicks;
                    return false;
                }

                // 下面这些**全部留在作用域内**：pawn.GetStatValue 会跑 StatPart 链
                // （其中有读 pawn.Map / MapHeld 的部分），出作用域就是同一个 null 键/NRE。
                Thing[] things;
                int[] counts;
                if (!ExtractIngredients(got, map, out things, out counts))
                {
                    // 原版选了地面上的料（核心里凑不齐）⇒ 用户拍板"物品在核心里才可以被自动化使用"
                    line.BlockKey = "DS_BA_NoCoreMaterial";
                    line.NextAcquireTick = now + CraftBackoffTicks;
                    return false;
                }

                line.Probe = got;
                line.Ingredients = things;
                line.Counts = counts;
                line.BaseRate = ComputeBaseRate(plan.recipe, bench, w);
                line.AllowedStuff = plan.allowedStuff;
                ApplyMaterial(line, plan);                      // ★ 限定材料：换进台子前把筛选器收紧
                line.StuffDef = UftStuffDef(plan.recipe, things);
                line.WorkAmount = WorkAmountFor(plan.recipe, line.Bill, things, got, line.StuffDef);
            line.WorkLeft = line.WorkAmount;
            line.NextAcquireTick = 0;
            line.BlockKey = null;
            Performance.DevDrawProfiler.Bump("账单取活", 1);

            // 与 JobDriver_DoBill 的两个 toil 对齐（:100 Notify_DoBillStarted / DoRecipeWork init:76）
            try
            {
                line.Bill.Notify_DoBillStarted(w);
                line.Bill.Notify_BillWorkStarted(w);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] bill.Notify_* 抛异常（已忽略）：" + e, 771133);
            }
            }
            finally
            {
                if (swapped) BillStackSwap.Restore(bench, original);
                DigitalWorkerScope.Exit(w);
            }
            return true;
        }

        /// <summary>
        /// 取活失败时用**便宜的只读判据**给面板一个具体原因。
        ///
        /// <para>为什么值得写：面板只显示"做不了"等于没说 —— 到底是缺料、资质不够、研究没了，
        /// 还是台子换不了栈？本 mod 在"诊断只打计数"上吃过亏（探针必须能自证）。</para>
        ///
        /// <para>⚠️ <c>Bill.PawnAllowedToStartAnew</c> 会写全局静态 <c>JobFailReason</c>（右键菜单用它显示
        /// "技能不符"之类），诊断完必须 <c>Clear()</c>。</para>
        /// </summary>
        private static string Diagnose(Thing bench, CraftPlan plan, Map map, Pawn w)
        {
            RecipeDef recipe = plan.recipe;

            if (!recipe.AvailableNow) return "DS_BA_Block_Research";
            if (recipe.FirstSkillRequirementPawnDoesntSatisfy(w) != null) return "DS_BA_Block_Skill";

            bool pawnOk = plan.lines.Count > 0 && plan.lines[0].Bill != null
                ? SafeAllowed(plan.lines[0].Bill, w)
                : SafeAllowedTransient(recipe, bench, w);
            if (!pawnOk) return "DS_BA_Block_Restricted";

            // 交互格被堵住/被禁止：原版 JobOnThing 在这里返回 null，与"缺料"是两件事。
            // ⚠️ IsForbidden 走活动区判定，需要一个"在图上"的 pawn（见调用处的注释）；
            //    另外它内部会拿 MapHeld 当字典键，出作用域就必须整段跳过 —— 宁可少报一种原因，也不能炸 tick。
            if (bench.def.hasInteractionCell && w != null && w.SpawnedOrAnyParentSpawned && w.MapHeld == map)
            {
                IntVec3 cell = bench.InteractionCell;
                if (cell.Impassable(map) || cell.IsForbidden(w)) return "DS_BA_Block_Spot";
            }
            return "DS_BA_Block_Material";
        }

        private static bool SafeAllowed(Bill_Production bill, Pawn w)
        {
            try
            {
                bool ok = bill.PawnAllowedToStartAnew(w);
                JobFailReason.Clear();
                return ok;
            }
            catch (Exception)
            {
                JobFailReason.Clear();
                return true;
            }
        }

        /// <summary>没有现成 bill 时（诊断路径）临时造一条来问原版同一个问题。</summary>
        private static bool SafeAllowedTransient(RecipeDef recipe, Thing bench, Pawn w)
        {
            try
            {
                Bill_Production bill = new Bill_Production(recipe);
                IBillGiver giver = bench as IBillGiver;
                if (giver != null)
                {
                    BillStack stack = new BillStack(giver);
                    stack.AddBill(bill);
                }
                return SafeAllowed(bill, w);
            }
            catch (Exception)
            {
                JobFailReason.Clear();
                return true;
            }
        }

        /// <summary>
        /// 原版速度公式（照抄 <c>Toils_Recipe.DoRecipeWork:109-113</c>）：
        /// <c>rate = workSpeedStat==null ? 1 : pawn.GetStatValue(workSpeedStat)</c>，
        /// 若配方有 <c>workTableSpeedStat</c> 且台子是 <c>Building_WorkTable</c> 再乘台子的该 stat。
        ///
        /// <para>**刻意不额外乘技能**：<c>workSpeedStat</c> 自己就吃技能（做饭类是 <c>CookSpeed</c>），
        /// 再乘一次就是双算。</para>
        /// </summary>
        private static float ComputeBaseRate(RecipeDef r, Thing bench, Pawn w)
        {
            float rate = (r.workSpeedStat == null) ? 1f : w.GetStatValue(r.workSpeedStat);
            Building_WorkTable table = bench as Building_WorkTable;
            if (r.workTableSpeedStat != null && table != null)
            {
                rate *= table.GetStatValue(r.workTableSpeedStat);
            }
            return rate;
        }

        /// <summary>
        /// 原版 <c>DoRecipeWork</c> 里 <c>workLeft = bill.GetWorkAmount(thing)</c> 的那个 <c>thing</c>：
        /// 取 <c>TargetIndex.B.Thing</c> —— 对非未完成品配方就是**最后一件被取走的原料**
        /// （<c>CollectIngredientsToils</c> 逐件设 targetB）。
        /// </summary>
        private static Thing LastIngredient(Thing[] things, Job job)
        {
            if (things != null && things.Length > 0) return things[things.Length - 1];
            return job.GetTarget(TargetIndex.B).Thing;
        }

        /// <summary>
        /// 把这条产线的"限定材料"落到那条临时账单上：除选中的材料外全部禁掉，
        /// 并把它设成**固定原料**（原版 <c>Bill.IsFixedOrAllowedIngredient</c> 对固定原料直接放行，
        /// 于是无论核心/地图/商队那边怎么筛，取料都只看这一种）。
        ///
        /// <para>只允许"配方本来就接受"的材料：既查配方槽位筛选器，也查
        /// <c>recipe.defaultIngredientFilter</c>（衣物默认禁金/银/玻璃钢等，只查前者会漏）。</para>
        /// </summary>
        private static void ApplyMaterial(CraftLine line, CraftPlan plan)
        {
            if (line == null || plan == null || plan.allowedStuff == null) return;
            Bill_Production bill = line.Bill;
            if (bill == null || bill.ingredientFilter == null) return;
            ThingDef want = plan.allowedStuff;

            List<ThingDef> all = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                ThingDef d = all[i];
                if (d == null || d == want) continue;
                bill.ingredientFilter.SetAllow(d, false);
            }
            bill.ingredientFilter.SetAllow(want, true);
        }

        /// <summary>配方到底接不接受这种材料（两层筛选都要过）。</summary>
        internal static bool RecipeAcceptsStuff(RecipeDef recipe, ThingDef d)
        {
            if (recipe == null || d == null || !d.IsStuff) return false;
            if (recipe.defaultIngredientFilter != null && !recipe.defaultIngredientFilter.Allows(d)) return false;
            if (recipe.ingredients != null)
            {
                for (int i = 0; i < recipe.ingredients.Count; i++)
                {
                    IngredientCount ic = recipe.ingredients[i];
                    if (ic == null || ic.IsFixedIngredient) continue;
                    if (ic.filter != null && ic.filter.Allows(d)) return true;
                }
            }
            // 没有可自选槽位时，退一步：只要默认筛选允许就算（例如整条配方只有一个固定原料）
            return recipe.ingredients == null || recipe.ingredients.Count == 0;
        }

        /// <summary>这条配方可以被选的材料（供界面用，已按两层筛选校验）。</summary>
        internal static List<ThingDef> UsableStuffs(RecipeDef recipe)
        {
            List<ThingDef> list = new List<ThingDef>();
            if (recipe == null) return list;
            List<ThingDef> all = DefDatabase<ThingDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                ThingDef d = all[i];
                if (d == null || !d.IsStuff) continue;
                if (!RecipeAcceptsStuff(recipe, d)) continue;
                list.Add(d);
            }
            list.Sort((a, b) => string.Compare(a.LabelCap, b.LabelCap, StringComparison.CurrentCulture));
            return list;
        }

        /// <summary>
        /// 未完成品（UFT）配方的<b>主材质</b> —— 复现原版
        /// <c>Toils_Recipe.MakeUnfinishedThingIfNeeded</c> 里的
        /// <c>ThingDef stuff = (recipe.unfinishedThingDef.MadeFromStuff ? thing.def : null);</c>
        /// （<c>thing</c> 即 dominant）。其余情况返回 null。
        /// </summary>
        private static ThingDef UftStuffDef(RecipeDef recipe, Thing[] things)
        {
            if (recipe == null || recipe.unfinishedThingDef == null) return null;
            if (!recipe.unfinishedThingDef.MadeFromStuff) return null;
            Thing dominant = BillCraftFunnel.DominantIngredient(recipe, things, null);
            return (dominant == null) ? null : dominant.def;
        }

        /// <summary>
        /// 这一轮的工作量。原版取值链（1.6）：
        /// <code>
        /// Bill.GetWorkAmount(thing) => recipe.WorkAmountTotal(thing) => recipe.WorkAmountForStuff(thing.Stuff)
        /// </code>
        /// 关键：**这条链只读 <c>thing.Stuff</c>**。原版在 UFT 配方上传的是那个未完成品（带着 stuff），
        /// 而代理以前传的是原料（<c>Thing.Stuff</c> 为 null）⇒ 工时整体跑偏（多数偏快）。
        /// 代理不需要造未完成品：<c>WorkAmountForStuff</c> 是 public 的，拿取活时定下的主材质问同一个方法，
        /// 结果与 <c>bill.GetWorkAmount(未完成品)</c> 相同。<b>普通配方仍然照抄原版</b>，不趁机改速度。
        /// </summary>
        private static float WorkAmountFor(RecipeDef recipe, Bill_Production bill, Thing[] things, Job got, ThingDef uftStuff)
        {
            if (recipe != null && recipe.unfinishedThingDef != null)
            {
                try
                {
                    return recipe.WorkAmountForStuff(uftStuff);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[DigitalStorage] 制作代理算不出未完成品配方的工作量（退回普通取值）："
                        + recipe.defName + " :: " + e, recipe.shortHash * 31 + 7717);
                }
            }
            return bill.GetWorkAmount(LastIngredient(things, got));
        }

        /// <summary>
        /// 把 <c>job.targetQueueB / countQueue</c> 抄成平行数组，并施加**反向守卫**：
        /// 每一件原料都必须住在**本图某个存储核心的容器**里（用户规则：材料只能在核心里）。
        ///
        /// <para>守卫是必要的：原版候选来源的第二处是"区域遍历地图上的散落物"
        /// （<c>WorkGiver_DoBill.cs:529-551</c>），核心里凑不齐时它会把**地上的东西**选进来。
        /// 不挡的话，制作代理就变成"第二套自动收纳"，直接从地上吃料。</para>
        /// </summary>
        private static bool ExtractIngredients(Job job, Map map, out Thing[] things, out int[] counts)
        {
            List<LocalTargetInfo> q = job.targetQueueB;
            int n = (q == null) ? 0 : q.Count;
            things = new Thing[n];
            counts = new int[n];

            for (int i = 0; i < n; i++)
            {
                Thing t = q[i].Thing;
                things[i] = t;
                counts[i] = (job.countQueue != null && i < job.countQueue.Count)
                    ? job.countQueue[i]
                    : (t != null ? t.stackCount : 0);

                if (t == null || t.Destroyed) return false;

                Building_StorageCore core = t.ParentHolder as Building_StorageCore;
                if (core == null || core.Map != map) return false;   // 地上的 / 别的图的 / 书架衣架里的
                if (counts[i] > t.stackCount) return false;          // 被人顺走了几件，数量对不上
            }
            return true;
        }
    }
}