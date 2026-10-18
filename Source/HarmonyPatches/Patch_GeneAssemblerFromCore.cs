using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>基因装配器造异种胚芽</b>把核心里的超凡胶囊算在内（<c>WorkGiver_CreateXenogerm</c>）。
    ///
    /// <para><b>断点</b>：<c>RimWorld\WorkGiver_CreateXenogerm.cs:26-33</c> 在
    /// <c>ArchitesRequiredNow &gt; 0</c> 时要求私有
    /// <c>FindArchiteCapsule</c>（<c>:61-64</c>）能找到一个超凡胶囊，否则写
    /// <c>"NoIngredient"</c> 并拒绝 —— 而这个查找是
    /// <c>GenClosest.ClosestThingReachable(..., ThingRequest.ForDef(ThingDefOf.ArchiteCapsule), ...)</c>，
    /// 没有 <c>lookInHaulSources</c>。胶囊全进核心 ⇒ 装配器显示"缺料"、异种胚芽永远造不出来。</para>
    ///
    /// <para><b>这里只塞候选、不物化</b>（与 <c>Patch_BloodTransfusionFromCore.cs</c> 同一判断）：
    /// <c>JobOnThing:48-56</c> 用 <c>JobMaker.MakeJob(JobDefOf.HaulToContainer, thing, t)</c>，
    /// 而 <c>Verse.AI\JobDriver_HaulToContainer.cs:137</c> 的
    /// <c>GotoThing(A, ClosestTouch, canGotoSpawnedParent: true)</c> 与
    /// <c>:141 StartCarryThing(..., canTakeFromInventory: true)</c>
    /// 原生支持"从容器里搬"（<c>Toils_Haul.cs:14</c> 的 <c>ErrorCheckForCarry</c> 只在
    /// 两个 flag 都没有时才拒容器物）。溢出来的胶囊不会被甩到地上，
    /// 也就不会重演"甩货→被自动收纳吸回→任务反复中断"的死循环。</para>
    ///
    /// <para><b>只影响判据与目标</b>：<c>HasJobOnThing</c> 通过后原版照旧建
    /// <c>HaulToContainer</c> job，数量由 <c>:54 Mathf.Min(ArchitesRequiredNow, thing.stackCount)</c>
    /// 自己算；<c>Patch_TradeDeal</c> 教训里那种"替原版算量"的写法一概不做。</para>
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_CreateXenogerm), "FindArchiteCapsule")]
    internal static class Patch_CreateXenogerm_FindArchiteCapsule
    {
        private static void Postfix(Pawn pawn, ref Thing __result)
        {
            if (__result != null) return;
            if (pawn?.Map == null) return;
            if (!ModsConfig.BiotechActive) return;
            if (!ConsumePatchUtil.PawnMayUseCore(pawn)) return;

            Thing best = HaulSourceContents.FindBestIncludingRemote(
                pawn.Map, null, t => t.def == ThingDefOf.ArchiteCapsule && !t.IsForbidden(pawn));
            if (best == null) return;

            __result = best;
        }
    }
}
