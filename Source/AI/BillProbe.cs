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
        /// <summary>取活失败（站位/资质/条件不满足）后的重试间隔。</summary>
        internal const int RetryTicks = 30;

        /// <summary>
        /// 材料面失败的退避（2 秒）：<c>DS_BA_Block_Material</c>（核心里真没料）、
        /// <c>DS_BA_NoCoreMaterial</c>（原版只能选到地上的料）、<c>DS_BA_Block_Claimed</c>（被别的线占着）。
        ///
        /// <para>⚠️ 这个值**同时是我们的性能保险**：核心里凑不齐时，原版
        /// <c>WorkGiver_DoBill.TryFindBestIngredientsHelper</c> 会在 <c>:525-527</c> 的核心候选失败后，
        /// 继续做 <b>全图区域遍历</b>（<c>:529-551</c>，<c>recipe.ingredientSearchRadius</c> 默认 999 ⇒ 整张图）。
        /// 原版自己用 <c>bill.nextTickToSearchForIngredients</c>（500~600 tick 负缓存）压这个成本；
        /// 我们每轮都把这个负缓存清掉（见下方 <c>ResetVanillaNegativeCache</c>），所以必须自己限频。</para>
        /// </summary>
        private const int CraftBackoffTicks = 120;

        /// <summary>
        /// 清掉原版写在我们这条 bill 上的<b>选料负缓存</b>。
        ///
        /// <para><b>为什么必须清</b>（2026-10-17 实机报告「制作代理莫名奇妙不识别，隔一会又好了」）：
        /// <c>WorkGiver_DoBill.StartOrResumeBillJob</c> 在 <c>:261-266</c> 里，只要
        /// <c>TryFindBestBillIngredients</c> 失败一次就会写
        /// <c>bill.nextTickToSearchForIngredients = now + ReCheckFailedBillTicksRange.RandomInRange</c>
        /// （<c>:95</c>，500~600 tick），而 <c>:194</c> 的硬闸会在
        /// <c>TicksGame &lt;= bill.nextTickToSearchForIngredients &amp;&amp; FloatMenuMakerMap.makingFor != pawn</c>
        /// 时**整条跳过这张 bill**（我们借台子时 <c>makingFor</c> 恒为 null ⇒ 永远命中这个分支）。</para>
        ///
        /// <para>⚠️ 我们这条 bill <b>不是临时的</b>：它由 <c>MakeLine</c> 一次创建、随 <see cref="CraftLine"/>
        /// 一直存活到 <c>ClearAll</c>（为了 <c>Notify_*</c> 与 HUD 进度）。所以原版这个缓存会**粘在它身上**：
        /// 任何一次取活失败（刚做完一件还没补料 / 另一条线占着这堆 / 料被顺走几件）之后，
        /// 接下来 8~10 秒（1×）代理对本线彻底失明，且退避期间面板照旧报「缺料」——
        /// 与用户看到的「不识别、隔一会又好了」完全吻合。</para>
        ///
        /// <para>* 顺带修一个更隐蔽的同源问题：不同 WorkGiverDef 派生类共用同一个 bill，
        /// 前一个 giver 失败写的缓存会让**同一次取活里的后一个 giver** 直接拿到 null。</para>
        /// </summary>
        private static void ResetVanillaNegativeCache(Bill bill)
        {
            if (bill != null) bill.nextTickToSearchForIngredients = 0;
        }

        private static int lastLogTick = -1;
        private static string lastLogKey;

        /// <summary>
        /// 调试日志（仅 <c>enableDebugLog</c>）：同一条线同一原因最多 300 tick 一条，
        /// 只在「原因变了」或「刚清掉负缓存」时刷屏才有价值 —— 报障时这段是唯一直接证据。
        /// </summary>
        private static void LogProbe(CraftLine line, bool ok, int stale, int now)
        {
            string block = ok ? "OK" : (line.BlockKey ?? "?");
            string recipe = (line.Bill != null && line.Bill.recipe != null) ? line.Bill.recipe.defName : "?";
            string key = block + "|" + recipe;
            if (key == lastLogKey && now - lastLogTick < 300) return;
            lastLogKey = key;
            lastLogTick = now;
            Log.Message("[DigitalStorage] 制作代理取活 " + (ok ? "成功" : "失败：" + block)
                + "｜配方 " + recipe
                + "｜台子 " + (line.Bench != null ? line.Bench.def.defName : "无")
                + (stale > 0 ? "｜清掉了原版负缓存（本来是 1.6 的 500~600 tick 致盲，还剩 " + stale + " tick）" : ""));
        }

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

        /// <summary>
        /// 有没有任何一个 DoBill giver 的 <c>fixedBillGiverDefs</c> 认领这张台子。
        ///
        /// <para>false = 原版口径下这张台子<b>谁都不能做 bill</b>（典型：mod 工作台）。
        /// 这种情况才需要放宽白名单（见 <see cref="BenchWhitelistScope"/>）——
        /// 原版台子必须走原路，否则"烹饪台只做烹饪"这类语义就没了。</para>
        /// </summary>
        private static bool AnyGiverClaimsBench(List<WorkGiverDef> defs, ThingDef benchDef)
        {
            if (defs == null || benchDef == null) return false;
            for (int i = 0; i < defs.Count; i++)
            {
                WorkGiverDef d = defs[i];
                if (d != null && d.fixedBillGiverDefs != null && d.fixedBillGiverDefs.Contains(benchDef)) return true;
            }
            return false;
        }

        /// <summary>
        /// 给一条在产线取一件活。返回 true 表示 <c>Ingredients/WorkLeft</c> 已就绪。
        ///
        /// <para>外面这一层只做一件事：**记录进门前原版负缓存还剩多少 tick**，再在调试日志里带出来。
        /// 这是「莫名奇妙不识别」类报障的第一现场证据（见 <see cref="ResetVanillaNegativeCache"/>）。</para>
        /// </summary>
        public static bool TryAcquire(CompBillAutomation comp, CraftPlan plan, CraftLine line, Map map, Pawn w, int now)
        {
            int stale = (line.Bill != null) ? line.Bill.nextTickToSearchForIngredients - now : 0;
            bool ok = TryAcquireInternal(comp, plan, line, map, w, now);
            if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog && (stale > 0 || !ok))
            {
                LogProbe(line, ok, stale, now);
            }
            return ok;
        }

        private static bool TryAcquireInternal(CompBillAutomation comp, CraftPlan plan, CraftLine line, Map map, Pawn w, int now)
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

                // 白名单：原版 WorkGiver_DoBill.ThingIsUsableBillGiver 只认 fixedBillGiverDefs 里的
                // defName（19 个 giver 里 16 个带精确列表）⇒ mod 工作台一个 giver 都不认。
                // 这里分两种情形：
                //   ① 有 giver 认领这张台子（原版台子，或某 mod 自己加了 workGiver）⇒ 只问它，行为与原版一致；
                //   ② 没有任何 giver 认领（mod 工作台）⇒ 打开 BenchWhitelistScope，让补丁把
                //      ThingIsUsableBillGiver 对这道门放宽（设置 craftAnyBench，默认开）。
                //      放宽后原版仍会用 bill.recipe.requiredGiverWorkType 挡掉不匹配的 giver。
                bool anyClaims = AnyGiverClaimsBench(defs, bench.def);
                bool relax = !anyClaims && DigitalStorage.Settings.DigitalStorageSettings.craftAnyBench;

                BenchWhitelistScope.Relaxed = relax;
                for (int i = 0; i < defs.Count; i++)
                {
                    WorkGiverDef d = defs[i];
                    if (!relax && d.fixedBillGiverDefs != null && !d.fixedBillGiverDefs.Contains(bench.def)) continue;

                    WorkGiver_DoBill doBill;
                    try { doBill = d.Worker as WorkGiver_DoBill; }
                    catch (Exception) { continue; }
                    if (doBill == null) continue;

                    Job candidate;
                    // 每次问原版之前都清一遍负缓存：上一个 giver / 上一轮失败写的 500~600 tick
                    // 会让这次的 JobOnThing 直接返回 null（见 ResetVanillaNegativeCache 的说明）。
                    ResetVanillaNegativeCache(line.Bill);
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
                // 白名单作用域只包这一圈取活（补丁 Prefix 靠这个 bool 决定放不放宽）
                BenchWhitelistScope.Relaxed = false;

                if (got == null)
                {
                    // ⚠️ 诊断必须在**作用域内**做：里面的 cell.IsForbidden(pawn) 会走
                    // Pawn_PlayerSettings.EffectiveAreaRestrictionInPawnCurrentMap，
                    // 而它拿 pawn.MapHeld 当字典键 —— 假 pawn 一旦出了作用域，MapHeld 是 null
                    // ⇒ Dictionary.TryGetValue(null) 抛 ArgumentNullException（2026-10-03 实机炸过）。
                    string block = Diagnose(bench, plan, map, w);

                    // 展开（方案 A：配方展开 / 一步成型）：原版报"缺料"时，把缺的中间产物摊成
                    // 我们自己的台子能做的原料，一步造出最终产物。只有材料面失败才值得试。
                    if (block == "DS_BA_Block_Material"
                        && RecipeExpansion.TryAcquire(comp, plan, line, map, w, now, bench))
                    {
                        return true;
                    }

                    line.BlockKey = block;
                    // 材料面失败会连带一次全图候选遍历（见 CraftBackoffTicks 注释），退得久一点；
                    // 站位/资质这类便宜失败 30 tick 就够了。
                    line.NextAcquireTick = now + (block == "DS_BA_Block_Material" ? CraftBackoffTicks : RetryTicks);
                    return false;
                }

                // 下面这些**全部留在作用域内**：pawn.GetStatValue 会跑 StatPart 链
                // （其中有读 pawn.Map / MapHeld 的部分），出作用域就是同一个 null 键/NRE。
                Thing[] things;
                int[] counts;
                if (!ExtractIngredients(got, line, map, out things, out counts, out bool claimedConflict))
                {
                    // 展开（方案 A）：核心里缺的是中间产物时，把中间件摊成它的原料再试一次。
                    // 只在这两条"材料面"失败上尝试 —— 站位/资质/研究不够时展开毫无意义。
                    if (!claimedConflict && RecipeExpansion.TryAcquire(comp, plan, line, map, w, now, bench))
                    {
                        return true;
                    }

                    // 原版选了地面上的料（核心里凑不齐）⇒ 用户拍板"物品在核心里才可以被自动化使用"
                    line.BlockKey = claimedConflict ? "DS_BA_Block_Claimed" : "DS_BA_NoCoreMaterial";
                    line.NextAcquireTick = now + CraftBackoffTicks;
                    return false;
                }

                // 先把原料与数量落位，再算速率/工作量：万一下面抛异常，
                // ClearWork（唯一的认领退出口）才有东西可退（B1）。
                line.Probe = got;
                line.Ingredients = things;
                line.Counts = counts;
                try
                {
                    line.BaseRate = ComputeBaseRate(plan.recipe, bench, w);
                    line.AllowedStuff = plan.allowedStuff;
                    ApplyMaterial(line, plan);                      // ★ 限定材料：换进台子前把筛选器收紧
                    line.StuffDef = UftStuffDef(plan.recipe, things);
                    line.WorkAmount = WorkAmountFor(plan.recipe, line.Bill, things, got, line.StuffDef);
                    line.WorkLeft = line.WorkAmount;
                    line.Expanded = false;
                    line.ExpandNote = null;
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[DigitalStorage] 制作代理估价失败（本轮作废，原料认领已退回）：" + e, 771134);
                    line.ClearWork();
                    line.BlockKey = "DS_BA_NoBill";
                    line.NextAcquireTick = now + RetryTicks;
                    return false;
                }

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
                BenchWhitelistScope.Relaxed = false;   // 兜底：异常路径也不能把作用域留在打开状态
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
        internal static float ComputeBaseRate(RecipeDef r, Thing bench, Pawn w)
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
        ///
        /// <para><b>def 与件数照抄原版，只换"从哪堆拿"</b>：原版挑中的那堆被别人按量认领掉、
        /// 或余量不够时，从本图核心同 def 的其它堆补齐（2026-10-23，实机 V-B0-5）。</para>
        /// </summary>
        private static bool ExtractIngredients(Job job, CraftLine owner, Map map, out Thing[] things, out int[] counts, out bool claimedConflict)
        {
            claimedConflict = false;
            things = new Thing[0];
            counts = new int[0];

            List<LocalTargetInfo> q = job.targetQueueB;
            int n = (q == null) ? 0 : q.Count;
            if (n == 0) return false;

            // ① 抄原版这一轮的账：每项 = (def, 需要几件, 它挑中的那一堆)。
            //    def 与件数**照抄原版**（营养换算/混料/取整全在后面算好了），只把"从哪堆拿"换掉。
            ThingDef[] defs = new ThingDef[n];
            int[] needs = new int[n];
            Thing[] preferred = new Thing[n];
            for (int i = 0; i < n; i++)
            {
                Thing t = q[i].Thing;
                if (t == null || t.Destroyed) return false;

                Building_StorageCore core = t.ParentHolder as Building_StorageCore;
                if (core == null || core.Map != map) return false;   // 地上的 / 别的图的 / 书架衣架里的

                int need = (job.countQueue != null && i < job.countQueue.Count)
                    ? job.countQueue[i]
                    : t.stackCount;
                if (need <= 0) return false;

                defs[i] = t.def;
                needs[i] = need;
                preferred[i] = t;
            }

            // ② 本图核心存货池，按 def 分组（大堆优先：少一次两堆拼）。
            //    这是"补料"的候选面 —— 原版挑中的那堆被别人认领掉时，从同 def 的别的堆拿。
            List<Thing> pool = new List<Thing>();
            HaulSourceContents.CollectDirect(map, pool);
            Dictionary<ThingDef, List<Thing>> byDef = new Dictionary<ThingDef, List<Thing>>();
            for (int i = 0; i < pool.Count; i++)
            {
                Thing t = pool[i];
                if (t == null || t.Destroyed || t.def == null || t.stackCount <= 0) continue;

                List<Thing> list;
                if (!byDef.TryGetValue(t.def, out list))
                {
                    list = new List<Thing>();
                    byDef[t.def] = list;
                }
                list.Add(t);
            }
            foreach (KeyValuePair<ThingDef, List<Thing>> kv in byDef)
            {
                kv.Value.Sort((a, b) => b.stackCount.CompareTo(a.stackCount));
            }

            // ③ 按量认领 + 跨堆补料。
            //
            // B1（审计 2026-10-17，对应第三方补丁 FIX-13）：**原料认领**。
            //
            // 不认领的后果（三份独立审计同时命中）：几条线对同一堆料各记一份账，
            // 谁先做完谁扣料 ⇒ 其余线下一 tick <c>StillValid</c> 数量对不上 ⇒ 整轮作废
            // ⇒ 并行塌缩成串行（台子越多越明显）。
            //
            // 用"认领"而非 ReservationManager：预约要 (thing, pawn)，而代理只有一个共享的
            // 资质载体 pawn，同一 pawn 再预约同一堆会被原版拒 ⇒ 还是塌缩（详见 CraftIngredientClaims）。
            //
            // ★ 2026-10-23（实机 V-B0-5）：认领改成**按量**。原版选料器不区分代理的"两条线"
            //   （共用假 pawn、不真预约），两条线必然同时挑中同一堆料；以前整堆互斥 ⇒ 第二条线
            //   永远判「这批材料正被另一条产线占用」。现在只要这堆够分就能并行，
            //   不够分则从同 def 的其它堆补齐（补不齐才算真缺料）。
            List<Thing> picked = new List<Thing>(n);
            List<int> nums = new List<int>(n);
            for (int i = 0; i < n; i++)
            {
                bool blocked;
                if (!Allocate(owner, defs[i], needs[i], preferred[i], byDef, picked, nums, out blocked))
                {
                    // 拿不到：把本次已认领的全退掉，等下一轮（原版会给这一堆换成别的堆再试）
                    CraftIngredientClaims.Release(owner);
                    things = new Thing[0];
                    counts = new int[0];
                    claimedConflict = blocked;
                    return false;
                }
            }

            things = picked.ToArray();
            counts = nums.ToArray();
            return things.Length > 0;
        }

        /// <summary>
        /// 给一项原料（<paramref name="def"/> 共 <paramref name="need"/> 件）在核心里落实实物。
        /// <b>原版挑中的那堆优先</b>，余量不够就从同 def 的其它堆补（可跨多堆拆分 —— 结算侧
        /// <c>BillCraftFunnel</c> 按 <c>SplitOff</c> 切料，本来就支持"一堆只拿一部分"）。
        ///
        /// <para><paramref name="blocked"/> 为 true 表示失败原因里有"被别人认领"的成分，
        /// 用来把 <c>DS_BA_Block_Claimed</c> 与 <c>DS_BA_NoCoreMaterial</c> 分开报。</para>
        /// </summary>
        private static bool Allocate(CraftLine owner, ThingDef def, int need, Thing preferred,
            Dictionary<ThingDef, List<Thing>> byDef, List<Thing> picked, List<int> nums, out bool blocked)
        {
            blocked = false;

            if (preferred != null && def != null && preferred.def == def && !preferred.Destroyed)
            {
                int free = CraftIngredientClaims.FreeAmountOf(preferred);
                if (free <= 0 && CraftIngredientClaims.ClaimedTotal(preferred) > 0) blocked = true;

                int take = (free < need) ? free : need;
                if (take > 0 && CraftIngredientClaims.TryClaim(preferred, owner, take))
                {
                    picked.Add(preferred);
                    nums.Add(take);
                    need -= take;
                }
            }
            if (need <= 0) return true;

            List<Thing> list;
            if (def == null || !byDef.TryGetValue(def, out list)) return false;

            for (int i = 0; i < list.Count && need > 0; i++)
            {
                Thing t = list[i];
                if (t == null || t.Destroyed || ReferenceEquals(t, preferred)) continue;

                int free = CraftIngredientClaims.FreeAmountOf(t);
                if (free <= 0)
                {
                    if (CraftIngredientClaims.ClaimedTotal(t) > 0) blocked = true;
                    continue;
                }

                int take = (free < need) ? free : need;
                if (!CraftIngredientClaims.TryClaim(t, owner, take)) continue;   // 账对不上就换下一堆

                picked.Add(t);
                nums.Add(take);
                need -= take;
            }

            if (need > 0 && CraftIngredientClaims.HasOtherOwner(preferred, owner)) blocked = true;
            return need <= 0;
        }
    }
}