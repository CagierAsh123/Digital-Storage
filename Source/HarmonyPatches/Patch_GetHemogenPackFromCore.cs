using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>血原质依赖者自己取包</b>：把核心里的血原质包算进 <c>JobGiver_GetHemogen</c> 的候选。
    ///
    /// <para><b>缺口</b>：<c>RimWorld/JobGiver_GetHemogen.cs:107</c> 的最后一行
    /// <c>GenClosest.ClosestThing_Global_Reachable(...)</c> 用的是
    /// <c>map.listerThings.ThingsOfDef(HemogenPack)</c> —— 只含已生成的包，
    /// 且 <c>canLookInHaulableSources</c> 未传（<c>Verse/GenClosest.cs:234</c> 默认 false）。</para>
    ///
    /// <para><b>为什么必须取出</b>：下游 <c>JobDriver_Ingest.cs:149/:173</c> 的 GotoThing 都没有
    /// <c>canGotoSpawnedParent</c> ⇒ 容器内容物塞进 job 会首 tick 静默失败。</para>
    ///
    /// <para><b>挂私有方法而不是 TryGiveJob</b>：<c>GetHemogenPack</c> 只负责"找包"，
    /// 让整块判定（要不要吃、吃几包）留在原版手里；我们只补最后那次搜索。
    /// 因为这个 giver 在 <c>ShouldConsumeHemogenNow</c> 长期为真时会每 think 调一次，
    /// 所以只在**算得出真的需要吃**（<c>needed &gt; 0</c>）时才动核心，避免无谓抽取。</para>
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_GetHemogen), "GetHemogenPack")]
    static class Patch_GetHemogenPackFromCore
    {
        static void Postfix(Pawn pawn, ref Thing __result)
        {
            if (__result != null) return;
            if (pawn?.MapHeld == null) return;
            if (!ConsumePatchUtil.PawnMayUseCore(pawn)) return;

            Gene_Hemogen gene = pawn.genes?.GetFirstGeneOfType<Gene_Hemogen>();
            if (gene == null) return;

            // 与 :77 同一个算式：还差几包的份量。
            int needed = Mathf.FloorToInt((gene.Max - gene.Value) / JobGiver_GetHemogen.HemogenPackHemogenGain);
            if (needed <= 0) return;

            Thing pack = HaulSourceContents.FindBestIncludingRemote(
                pawn.MapHeld, null,
                t => t.def == ThingDefOf.HemogenPack
                     && !t.IsForbidden(pawn)
                     && pawn.CanReserve(t));
            if (pack == null) return;

            Thing taken = HaulSourceContents.ExtractToFeet(pack, Mathf.Min(needed, pack.stackCount), pawn);
            if (taken != null) __result = taken;
        }
    }
}
