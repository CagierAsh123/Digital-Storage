using System;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 消费类 JobGiver 的共用入口：给定 pawn 的需求，在容器里找一件匹配的**真实 Thing**，创 job。
    /// 返回 null = 容器里没有可用的 → patch 不干扰原版的 null 行为。
    ///
    /// <para><b>4.0 与 3.0 的差别</b>：3.0 扫的是账本 <c>(def, stuff) → 数量</c>，
    /// 取料要"扣账 + spawn"；4.0 直接拿着那件真实的 Thing，取出即可。
    /// 因此不再需要 <c>ItemKey</c>、不再需要在 job 上挂"计划"（见
    /// <c>JobDriver_DS_Consume</c>：Thing 就放在 <c>job.targetA</c>）。</para>
    /// </summary>
    public static class ConsumptionHelper
    {
        /// <summary>
        /// 诊断用：上一次 <see cref="TryCreateJob(Pawn, Predicate{Thing})"/> 为什么没产出 job。
        /// 只在 dev 模式下由调用方打日志 —— 读代码读不出来的时候，让运行时说实话。
        /// </summary>
        public static string LastFailReason = "not called";

        /// <summary>找某个特定 ThingDef（drug 等精确匹配用）。</summary>
        public static Job TryCreateJob(Pawn pawn, ThingDef desiredDef)
        {
            return TryCreateJob(pawn, t => t.def == desiredDef);
        }

        /// <summary>用自定义 predicate 找（食物用：任何可食即可）。</summary>
        public static Job TryCreateJob(Pawn pawn, Predicate<Thing> filter)
        {
            if (pawn?.Map == null) { LastFailReason = "pawn/map null"; return null; }
            if (filter == null) { LastFailReason = "filter null"; return null; }

            // 8.1 bugfix: 机械体不消费任何 ingestible（无食物/药物/娱乐需求），
            // 统一排除 —— 所有消费 patch 都走这个入口，防止机械体从核心吃食物。
            if (pawn.RaceProps.IsMechanoid) { LastFailReason = "mechanoid"; return null; }

            // 【4.0 产品决策 · 砍芯片】这里原先有一道门：
            //   if (!HasTerminalImplant(pawn) && Settings.requireChipForCoreAccess) return null;
            // 用户拍板「核心一放就是完全体，全部无损直接隔空获取，不需要任何额外 hediff / 建筑」，
            // 所以那道门已删 —— 它正是「重进存档后既不吃饭也不吃药」的根因，实测日志：
            //   [DS] GetFood: null (no chip and requireChipForCoreAccess=on)
            // 芯片实体（Hediff_TerminalImplant / DigitalStorage_TerminalChip）与
            // Settings.requireChipForCoreAccess 也已一并删除。

            // 社区反馈「食物方案禁止吃虫胶也没用」：原版 JobGiver_GetFood →
            // FoodUtility.TryFindBestFoodSourceFor 会对每个候选调 FoodUtility.WillEat
            // （FoodIsSuitable + FoodPolicy.Allows + 泰特托 + 圣兽肉 + 头衔），
            // 本 mod 直连容器绕过了这一层，这里补上同一套 gate。
            bool allowDrug = !pawn.IsTeetotaler();
            Map map = pawn.Map;
            ReservationManager resMgr = map.reservationManager;
            int seen = 0, rejectedByFilter = 0, rejectedByGate = 0, rejectedByReserved = 0;

            Thing best = HaulSourceContents.FindBestIncludingRemote(
                map,
                t => FoodScoring.Score(pawn, t.def),
                t =>
                {
                    seen++;
                    if (!filter(t)) { rejectedByFilter++; return false; }
                    ThingDef def = t.def;
                    if (def == null) return false;

                    if (def.IsNutritionGivingIngestible)
                    {
                        if (!pawn.WillEat(t, pawn, careIfNotAcceptableForTitle: true)) { rejectedByGate++; return false; }
                    }
                    else if (def.IsDrug)
                    {
                        // 成瘾品 / 娱乐性药物：不贪食者与变体限制（对齐原版 JobGiver_GetFood 的 allowDrug）
                        if (!allowDrug) { rejectedByGate++; return false; }
                        if (!pawn.DrugIsSuitable(def)) { rejectedByGate++; return false; }
                    }

                    // 已被别人预订的不要选（原版 GenClosest 也会做这个检查）
                    if (resMgr != null && resMgr.IsReserved(t)) { rejectedByReserved++; return false; }
                    return true;
                });

            if (best == null)
            {
                LastFailReason = "no match (seen=" + seen + " filtered=" + rejectedByFilter
                    + " gate=" + rejectedByGate + " reserved=" + rejectedByReserved + ")";
                return null;
            }

            int take = GetIngestAmount(pawn, best.def, best.stackCount);
            if (take <= 0) { LastFailReason = "GetIngestAmount<=0 for " + best.def.defName; return null; }

            LastFailReason = "ok: " + best.def.defName + " x" + take;

            var job = JobMaker.MakeJob(DigitalStorage_JobDefOf.DigitalStorage_Consume);
            // targetA = 要吃/要用的那件真实东西（会被 Scribe，存档读档不丢）
            job.SetTarget(TargetIndex.A, best);
            // targetC = 它所在的容器（用于 FailOn 的电力/存在性检查）
            Thing holder = best.ParentHolder as Thing;
            if (holder != null) job.SetTarget(TargetIndex.C, holder);
            job.count = take;
            return job;
        }

        /// <summary>按吃饭/吃药的需求算一次取多少，不贪心拿满。</summary>
        private static int GetIngestAmount(Pawn pawn, ThingDef def, int maxAvailable)
        {
            if (def.ingestible == null) return 0;

            int take;
            if (def.IsNutritionGivingIngestible && pawn.needs?.food != null)
            {
                // 食物：按饥饿度算需要多少
                float nutritionPerItem = def.GetStatValueAbstract(StatDefOf.Nutrition);
                take = FoodUtility.StackCountForNutrition(pawn.needs.food.NutritionWanted, nutritionPerItem);
                if (def.ingestible.maxNumToIngestAtOnce > 0)
                    take = Math.Min(take, def.ingestible.maxNumToIngestAtOnce);
            }
            else
            {
                take = def.ingestible.defaultNumToIngestAtOnce;
                if (take <= 0) take = 1;
            }
            return Math.Min(take, maxAvailable);
        }
    }
}
