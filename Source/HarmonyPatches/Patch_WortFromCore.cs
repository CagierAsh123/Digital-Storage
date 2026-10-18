using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>给发酵桶加麦芽汁</b>把核心里的麦芽汁算在内（<c>WorkGiver_FillFermentingBarrel</c>）。
    ///
    /// <para><b>断点</b>：<c>RimWorld\WorkGiver_FillFermentingBarrel.cs:62-66</c> 的私有
    /// <c>FindWort</c> 用
    /// <c>GenClosest.ClosestThingReachable(..., ThingRequest.ForDef(ThingDefOf.Wort), ...)</c>
    /// —— 没传 <c>lookInHaulSources</c>，容器的麦芽汁它看不见。两条调用链都汇到这里：
    /// <c>HasJobOnThing:43</c>（判据，失败写 <c>NoWort</c>）与 <c>JobOnThing:58</c>（真建 job）。</para>
    ///
    /// <para><b>必须物化</b>：下游 <c>JobDriver_FillFermentingBarrel.cs:39-40</c> 是
    /// <c>GotoThing(B, ClosestTouch)</c> + <c>StartCarryThing(B)</c>，
    /// 没有 <c>canGotoSpawnedParent</c>、也没传 <c>canTakeFromInventory</c>
    /// （<c>Toils_Haul.cs:14</c> 会直接 <c>EndCurrentJob(Incompletable)</c>）⇒ 容器里的麦芽汁必须
    /// 先取到脚下。</para>
    ///
    /// <para><b>取多少</b>：驱动自己会写 <c>job.count = Barrel.SpaceLeftForWort</c>
    /// （<c>JobDriver_FillFermentingBarrel.cs:35</c>），所以这里取
    /// <c>Min(桶还差多少, 核心这堆数量)</c>，多取只会在桶满后剩在地上。</para>
    ///
    /// <para><b>幂等</b>：探针（<c>HasJobOnThing</c>）先调一次 → 取到脚下后麦芽汁已在地图上，
    /// <c>JobOnThing</c> 再调时原版自己就能找到它，不会二次抽取。</para>
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_FillFermentingBarrel), "FindWort")]
    internal static class Patch_FillFermentingBarrel_FindWort
    {
        private static void Postfix(Pawn pawn, Building_FermentingBarrel barrel, ref Thing __result)
        {
            if (__result != null) return;      // 地图上已有麦芽汁 ⇒ 原版结果照用
            if (pawn?.Map == null || barrel == null) return;
            if (!ConsumePatchUtil.PawnMayUseCore(pawn)) return;

            int need = barrel.SpaceLeftForWort;
            if (need <= 0) return;

            Thing best = HaulSourceContents.FindBestIncludingRemote(
                pawn.Map, null, t => t.def == ThingDefOf.Wort && !t.IsForbidden(pawn));
            if (best == null) return;

            Thing placed = HaulSourceContents.ExtractToFeet(best, Mathf.Min(need, best.stackCount), pawn);
            if (placed == null) return;

            if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog && ConsumePatchUtil.ShouldLog("WortFromCore"))
            {
                Log.Message("[DigitalStorage] 发酵桶从核心取麦芽汁: x" + placed.stackCount + " → " + pawn.LabelShort);
            }
            __result = placed;
        }
    }
}
