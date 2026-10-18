using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>生物塑形舱的常规营养装填</b>把核心里的食物算在内（<c>WorkGiver_HaulToBiosculpterPod</c>）。
    ///
    /// <para><b>断点</b>：<c>RimWorld\WorkGiver_HaulToBiosculpterPod.cs:68-76</c> 的私有
    /// <c>FindNutrition</c> 用
    /// <c>GenClosest.ClosestThingReachable(..., ThingRequest.ForGroup(FoodSourceNotPlantOrTree), ...)</c>
    /// —— 没传 <c>lookInHaulSources</c>，核心里的食物看不见。两条调用链都汇到这里：
    /// <c>HasJobOnThing:38</c>（判据，失败写 <c>NoFood</c>）与 <c>JobOnThing:57</c>（真建 job）。</para>
    ///
    /// <para><b>只塞候选、不物化</b>：<c>JobOnThing:60</c> 用
    /// <c>HaulAIUtility.HaulToContainerJob(pawn, thing, t)</c> ⇒
    /// <c>JobDriver_HaulToContainer.cs:137/:141</c> 的
    /// <c>canGotoSpawnedParent</c> + <c>canTakeFromInventory</c> 原生支持从容器搬
    /// （<c>Toils_Haul.cs:14</c>）。食物不会被甩到地上，也就不会引发"甩货→被自动收纳吸回"的循环。</para>
    ///
    /// <para><b>与 <c>WorkGiver_DoBill</c> 系补丁的关系</b>：塑形舱<b>配方</b>（医疗/愉悦/再生等循环）
    /// 的固定原料早已由 <c>Patch_WorkGiver_DoBill_RemoteIngredients.cs</c> 覆盖；
    /// 这里补的是另一条完全独立的入口 —— 装填<b>营养</b>（<c>CompBiosculpterPod.RequiredNutritionRemaining</c>）。</para>
    ///
    /// <para><b>数量口径照抄原版</b>：<c>:75</c>
    /// <c>Mathf.CeilToInt(RequiredNutritionRemaining / 单个营养值)</c>，再与堆叠数取小；
    /// 我们只把"地图上没有食物"换成"核心里有食物"。</para>
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_HaulToBiosculpterPod), "FindNutrition")]
    internal static class Patch_HaulToBiosculpterPod_FindNutrition
    {
        private static void Postfix(Pawn pawn, CompBiosculpterPod pod, ref ThingCount __result)
        {
            if (__result.Thing != null) return;      // 地图上已有食物 ⇒ 原版结果照用
            if (pawn?.Map == null || pod == null) return;
            if (!ConsumePatchUtil.PawnMayUseCore(pawn)) return;

            float need = pod.RequiredNutritionRemaining;
            if (need <= 0f) return;

            Thing best = HaulSourceContents.FindBestIncludingRemote(pawn.Map, null, t =>
                !t.IsForbidden(pawn)
                && t.def.IsNutritionGivingIngestible
                && NutritionOf(t) > 0f
                && pod.CanAcceptNutrition(t)          // 原版 :83，尊重舱的营养筛选
                && pawn.CanReserve(t));               // 原版 :79

            if (best == null) return;

            float per = NutritionOf(best);
            int count = Mathf.Clamp(Mathf.CeilToInt(need / per), 1, best.stackCount);
            __result = new ThingCount(best, count);

            if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog && ConsumePatchUtil.ShouldLog("BiosculpterFromCore"))
            {
                Log.Message("[DigitalStorage] 塑形舱从核心取营养: " + best.def.defName + " x" + count
                    + " (缺 " + need.ToString("0.##") + " 营养) → " + pawn.LabelShort);
            }
        }

        private static float NutritionOf(Thing t)
        {
            return t.GetStatValue(StatDefOf.Nutrition);
        }
    }
}
