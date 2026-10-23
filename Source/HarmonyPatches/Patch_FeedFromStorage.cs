using System;
using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// G4: 喂食病人/囚犯时，食物从容器取。
    ///
    /// 原版 <c>FoodUtility.TryFindBestFoodSourceFor</c> 只在 map + 背包里搜食物 →
    /// 此 Postfix 在找不到时扫容器 → 取出放到喂食者脚下。
    /// 按 <c>FoodScoring.Score</c> 降序选最优食物。
    /// </summary>
    [HarmonyPatch(typeof(FoodUtility), "TryFindBestFoodSourceFor")]
    static class Patch_FeedFromStorage
    {
        static void Postfix(Pawn getter, Pawn eater, ref Thing foodSource, ref ThingDef foodDef, ref bool __result)
        {
            if (__result || foodSource != null) return;
            if (getter?.Map == null || eater == null) return;

            // 囚犯不能用核心（用户 2026-10-02 拍板）。判的是**取用者**：
            // 典狱长给囚犯取饭时 getter = 典狱长 ⇒ 放行（饭照样从核心取、送进牢房）；
            // 囚犯自己找饭时 getter = 囚犯 ⇒ 挡住，让他吃牢房里送来的那份。
            if (!ConsumePatchUtil.PawnMayUseCore(getter)) return;

            // 【4.0 砍芯片】这里原先有一道门：
            //   if (!HasTerminalImplant(getter) && !isCaregiving) return;
            // 现在拆掉 —— 用户拍板核心一放就是完全体，任何人喂食都能从核心取。

            ThingFilter foodFilter = null;
            if (eater.foodRestriction != null)
            {
                var policy = eater.foodRestriction.GetCurrentRespectedRestriction(getter);
                if (policy != null) foodFilter = policy.filter;
            }
            ThingFilter capturedFilter = foodFilter;

            Thing best = HaulSourceContents.FindBestIncludingRemote(
                getter.Map,
                // A2：评分也用带 WillEat 的那种重载 —— 评分与下面的谓词必须同口径，
                // 否则会出现"评分认为最好、但这个人根本不能吃"的候选。
                t => FoodScoring.Score(eater, t, getter),
                t => t.def.IsNutritionGivingIngestible && t.def.IsIngestible
                     && (capturedFilter == null || capturedFilter.Allows(t.def))
                     && eater.RaceProps.Eats(t.def.ingestible.foodType)
                     // A2（审计 2026-10-17）：原版 TryFindBestFoodSourceFor 对每个候选都会过
                     // FoodUtility.WillEat（食物方案/戒律/头衔/婴儿不能吃固体/生食厌恶…）。
                     // 我们直连容器绕过了这一层 ⇒ 抽到脚下后下游判据再次拒绝 ⇒ 食物撒一地、
                     // 喂食 job 反复重试（补丁仓库 v0.65 实测抽取次数 ×2→×53）。
                     && eater.WillEat(t, getter, careIfNotAcceptableForTitle: true));

            if (best == null) return;

            // 第二道闸：即使上面因将来改动漏了谓词，也绝不在"不能吃"的前提下去抽取。
            // 铁律：ExtractToFeet 是有副作用的（物品真会离开容器落地），必须先确认能用。
            if (!eater.WillEat(best, getter, careIfNotAcceptableForTitle: true)) return;

            Thing taken = TakeForFeeding(best, eater, getter);
            if (taken == null) return;

            foodSource = taken;
            foodDef = taken.def;
            __result = true;
        }

        /// <summary>复用窗口（tick）：探测与真正建 job 之间通常只隔同一个 tick 或下一 tick。</summary>
        private const int ReuseTicks = 2;

        private struct Prepared
        {
            public Pawn Eater;
            public Thing Thing;
            public int Tick;
        }

        /// <summary>同一个取用者（getter）刚抽出来的那一份。</summary>
        private static readonly Dictionary<Pawn, Prepared> preparedByGetter = new Dictionary<Pawn, Prepared>();

        /// <summary>
        /// 抽一份可喂的食物到 <paramref name="getter"/> 脚下，并做 <see cref="ReuseTicks"/> 的复用。
        ///
        /// <para><b>为什么必须复用</b>（用户 2026-10-23 报「每次都会多留一个婴儿食品在地上」）：
        /// 原版 <c>WorkGiver_Scanner.cs:35-37</c> 把 <c>HasJobOnThing</c> 实现成
        /// <c>JobOnThing(pawn, t, forced) != null</c> —— 探测调一次、真正建 job 再调一次，
        /// 而 <c>WorkGiver_BottleFeedBaby.JobOnThing</c>（<c>:15</c>）会走
        /// <c>ChildcareUtility.FindBabyFoodForBaby</c>（<c>:772</c>）→ 落到本补丁的挂点上。
        /// 探测那一次抽出来的东西没人用 ⇒ 地上白留一份。
        /// 同一个坑在 <c>Patch_WardenDeliverHemogenFromCore</c> 已经踩过（那里也是 2 tick 复用窗口）。</para>
        /// </summary>
        private static Thing TakeForFeeding(Thing best, Pawn eater, Pawn getter)
        {
            int now = Find.TickManager.TicksGame;

            if (preparedByGetter.TryGetValue(getter, out Prepared prepared))
            {
                preparedByGetter.Remove(getter);
                if (prepared.Thing != null && !prepared.Thing.Destroyed && prepared.Thing.Spawned
                    && prepared.Thing.Map == getter.MapHeld
                    && prepared.Eater == eater && now - prepared.Tick <= ReuseTicks)
                {
                    return prepared.Thing;
                }
            }

            int take = CalculateFeedAmount(eater, best);
            if (take <= 0) return null;

            Thing taken = HaulSourceContents.ExtractToFeet(best, take, getter);
            if (taken == null) return null;

            preparedByGetter[getter] = new Prepared { Eater = eater, Thing = taken, Tick = now };
            if (preparedByGetter.Count > 32) PruneStale(now);
            return taken;
        }

        /// <summary>纯内存卫生：只探测、没真正建 job 的取用者会留下一条过期项。</summary>
        private static void PruneStale(int now)
        {
            var stale = new List<Pawn>();
            foreach (KeyValuePair<Pawn, Prepared> kv in preparedByGetter)
            {
                if (kv.Key == null || kv.Value.Thing == null || kv.Value.Thing.Destroyed
                    || now - kv.Value.Tick > 120)
                {
                    stale.Add(kv.Key);
                }
            }
            for (int i = 0; i < stale.Count; i++) preparedByGetter.Remove(stale[i]);
        }

        /// <summary>
        /// 取多少——<b>必须与原版同一个算式</b>。
        ///
        /// <para>原版 <c>JobDriver_BottleFeedBaby</c>（<c>:36-40</c>）拿食物时用的是
        /// <c>Mathf.Min(babyFood.stackCount, FoodUtility.WillIngestStackCountOf(baby, def, NutritionForEater(baby, babyFood)))</c>，
        /// 而 <c>FoodUtility.StackCountForNutrition</c>（<c>FoodUtility.cs:1307-1315</c>）是
        /// <c>Mathf.RoundToInt</c>（四舍五入）、下限 1；旧实现用的是 <c>Math.Ceiling</c> 且拿的是
        /// <c>GetStatValueAbstract(StatDefOf.Nutrition)</c>。只要「营养需求 ÷ 单件营养」的小数部分 &lt; 0.5，
        /// 旧实现就比 job 实际会拿走的**多抽一件** ⇒ 那一件正好留在地上（用户看到的就是这个）。</para>
        /// </summary>
        private static int CalculateFeedAmount(Pawn eater, Thing food)
        {
            if (food?.def == null) return 0;
            float nutritionPerItem = FoodUtility.NutritionForEater(eater, food);
            int needed = FoodUtility.WillIngestStackCountOf(eater, food.def, nutritionPerItem);
            if (needed < 1) needed = 1;
            return Math.Min(needed, food.stackCount);
        }
    }
}
