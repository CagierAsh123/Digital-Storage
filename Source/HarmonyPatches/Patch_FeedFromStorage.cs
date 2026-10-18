using System;
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

            int take = CalculateFeedAmount(eater, best.def, best.stackCount);
            if (take <= 0) return;

            Thing taken = HaulSourceContents.ExtractToFeet(best, take, getter);
            if (taken == null) return;

            foodSource = taken;
            foodDef = taken.def;
            __result = true;
        }

        private static int CalculateFeedAmount(Pawn eater, ThingDef def, int maxAvailable)
        {
            float nutritionWanted = eater.needs?.food != null
                ? eater.needs.food.NutritionWanted
                : 1f;
            float nutritionPerItem = def.GetStatValueAbstract(StatDefOf.Nutrition);
            if (nutritionPerItem <= 0f) return 1;
            int needed = (int)Math.Ceiling(nutritionWanted / nutritionPerItem);
            if (needed <= 0) needed = 1;
            if (def.ingestible.maxNumToIngestAtOnce > 0)
                needed = Math.Min(needed, def.ingestible.maxNumToIngestAtOnce);
            return Math.Min(needed, maxAvailable);
        }
    }
}
