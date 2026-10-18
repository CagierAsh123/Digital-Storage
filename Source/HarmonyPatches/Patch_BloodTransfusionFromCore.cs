using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>输血手术</b>把核心里的血原质包算在内。
    ///
    /// <para><b>两处硬缺口</b>（<c>RimWorld/Recipe_BloodTransfusion.cs</c>）：
    /// <c>:27</c> <c>AvailableOnNow</c> 只看
    /// <c>thing.MapHeld.listerThings.ThingsOfDef(HemogenPack).Count == 0</c> ⇒ 包全进核心后
    /// <b>手术在列表里直接不可用</b>；<c>:87</c> <c>GetIngredientCount</c> 也只数地图上的包
    /// ⇒ 即便放行，需要的包数也算成 0。</para>
    ///
    /// <para><b>只修判定与数量，不碰取料</b>：<c>WorkGiver_DoBill.cs:481-496</c> 本身就遍历
    /// <c>map.haulDestinationManager.AllHaulSourcesListForReading</c>（我们的核心是
    /// <c>IHaulSource</c>，天然在内），而 <c>JobDriver_DoBill.cs:121/:123</c> 有
    /// <c>canGotoSpawnedParent</c> + <c>canTakeFromInventory</c> ⇒ 材料自己就能从容器进手术台。
    /// <b>历史上"顺手把材料取到地上"的写法被实测证明有害</b>（整堆外甩 + 被自动收纳吸回，
    /// 反复中断手术），这里绝不重犯。</para>
    ///
    /// <para><b>保守性</b>：<c>AvailableOnNow</c> 只在「地图上确实一个已生成的包都没有 + 病人有失血 +
    /// 核心里有包」时翻案；<c>GetIngredientCount</c> 只在原值未达上限时补核心数量再与上限取小。</para>
    /// </summary>
    static class Patch_BloodTransfusionFromCore
    {
        internal static int CountPacksInStorage(Map map)
        {
            return HaulSourceContents.CountOf(map, ThingDefOf.HemogenPack);
        }
    }

    /// <summary>① 可用性判定：原版 <c>:27</c> 的缺口。</summary>
    [HarmonyPatch(typeof(Recipe_BloodTransfusion), nameof(Recipe_BloodTransfusion.AvailableOnNow))]
    static class Patch_BloodTransfusion_AvailableOnNow
    {
        static void Postfix(Recipe_BloodTransfusion __instance, Thing thing, ref bool __result)
        {
            if (__result) return;

            if (!(thing is Pawn patient) || patient.health?.hediffSet == null) return;  // :23 + base 的 `thing is Pawn`
            Map map = thing.MapHeld;
            if (map == null) return;
            if (!patient.health.hediffSet.HasHediff(HediffDefOf.BloodLoss)) return;      // :31

            // 原版拒绝可能不是"没有已生成的包"，而是 base 的某一条（动物 / 性别 / 年龄 …）。
            // 那些条件必须自己重跑一遍，否则会给动物或变异体开出原版根本不允许的手术。
            RecipeDef recipe = __instance.recipe;
            if (recipe != null && !BaseSurgeryChecksPass(recipe, patient)) return;

            // 地图上其实有包却仍被拒 ⇒ 是别的闸门拒的（:35 base），不推翻。
            if (map.listerThings.ThingsOfDef(ThingDefOf.HemogenPack).Count > 0) return;

            if (Patch_BloodTransfusionFromCore.CountPacksInStorage(map) <= 0) return;

            __result = true;
        }

        /// <summary>
        /// <c>Recipe_Surgery.AvailableOnNow</c> 的判定条件（`RimWorld\Recipe_Surgery.cs:26-68`）的镜像。
        /// 我们只推翻"地图上没有已生成的血包"这一条，其余条件必须自己过一遍。
        /// **原版此处若增删条件，这里要同步**。
        /// </summary>
        private static bool BaseSurgeryChecksPass(RecipeDef recipe, Pawn pawn)
        {
            if ((recipe.genderPrerequisite ?? pawn.gender) != pawn.gender) return false;
            if (recipe.mustBeFertile && pawn.Sterile()) return false;
            if (!recipe.allowedForQuestLodgers && pawn.IsQuestLodger()) return false;
            if (recipe.minAllowedAge > 0 && pawn.ageTracker.AgeBiologicalYears < recipe.minAllowedAge) return false;
            if (recipe.developmentalStageFilter.HasValue
                && !recipe.developmentalStageFilter.Value.Has(pawn.DevelopmentalStage)) return false;
            if (recipe.humanlikeOnly && !pawn.RaceProps.Humanlike) return false;
            if (ModsConfig.AnomalyActive)
            {
                if (recipe.mutantBlacklist != null && pawn.IsMutant && recipe.mutantBlacklist.Contains(pawn.mutant.Def)) return false;
                if (recipe.mutantPrerequisite != null && (!pawn.IsMutant || !recipe.mutantPrerequisite.Contains(pawn.mutant.Def))) return false;
            }
            return true;
        }
    }

    /// <summary>② 用量计算：原版 <c>:87</c> 的缺口。</summary>
    [HarmonyPatch(typeof(Recipe_BloodTransfusion), nameof(Recipe_BloodTransfusion.GetIngredientCount))]
    static class Patch_BloodTransfusion_GetIngredientCount
    {
        static void Postfix(IngredientCount ing, Bill bill, ref float __result)
        {
            if (bill?.Map == null || ing?.filter == null) return;

            // 只处理血原质这一项配料（该配方只有这一项，保持一般性）。
            if (!ing.filter.Allows(ThingDefOf.HemogenPack)) return;

            // 原版只在「billGiver 是 Pawn 且有失血」时才走 min(地图包数, 失血量/0.35) 这条路径，
            // 其余情况返回的是 base 值，我们不该动（:78-:86）。
            if (!(bill.billStack?.billGiver is Pawn patient) || patient.health?.hediffSet == null) return;
            Hediff loss = patient.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.BloodLoss);
            if (loss == null) return;

            float cap = loss.Severity / Recipe_BloodTransfusion.BloodlossHealedPerPack;
            if (__result >= cap) return;                                  // 已达上限，加多少都一样

            int stored = Patch_BloodTransfusionFromCore.CountPacksInStorage(bill.Map);
            if (stored <= 0) return;

            __result = Mathf.Min(__result + stored, cap);
        }
    }
}
