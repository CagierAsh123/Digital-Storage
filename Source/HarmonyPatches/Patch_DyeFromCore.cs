using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>染发（<c>JobGiver_DyeHair</c>）与衣物改色（<c>JobGiver_OptimizeApparel.TryCreateRecolorJob</c>）</b>
    /// 把核心里的染料算在内。
    ///
    /// <para><b>断点</b>：两者都直接扫地图上的染料 ——
    /// <c>RimWorld\JobGiver_DyeHair.cs:23</c> 的
    /// <c>GenClosest.ClosestThing_Global_Reachable(..., listerThings.ThingsOfDef(Dye), ...)</c>
    /// 与 <c>RimWorld\JobGiver_OptimizeApparel.cs:197</c> 的
    /// <c>pawn.Map.listerThings.ThingsOfDef(ThingDefOf.Dye)</c>。染料全进核心 ⇒ 两者都拿不到染料：
    /// 前者 <c>:24-27</c> 直接 <c>return null</c>，后者 <c>tmpQueueDye</c> 空 ⇒
    /// <c>tmpQueueApparel</c> 空 ⇒ <c>:253</c> <c>return false</c>。</para>
    ///
    /// <para><b>为什么用"先物化"而不是"塞候选"</b>：
    /// 染发驱动 <c>JobDriver_DyeHair.cs</c> 走 <c>GotoThing(B)</c> 取手上的染料；
    /// 改色驱动 <c>JobDriver_RecolorApparel.cs:53</c> 虽然用的是
    /// <c>JobDriver_DoBill.CollectIngredientsToils</c>（A 管线），但它的 job 是由
    /// <c>TryCreateRecolorJob</c> 用 <c>out Job</c> 产出的 —— 在 out 参数上做 Harmony 注入是
    /// 本轮刻意避开的翻车点（见 <c>Docs\实现方案-补全功能-2026-10-17.md</c> §12）。
    /// 统一改成"进门前先取到脚下"，两个入口共用一条已验证的路径。</para>
    ///
    /// <para><b>严禁在 <c>dryRun</c> 时抽取</b>：<c>RimWorld\Building_StylingStation.cs:30</c>
    /// 用 <c>TryCreateRecolorJob(pawn, out _, dryRun: true)</c> 做预览，那一支必须零副作用。</para>
    ///
    /// <para><b>取值量</b>：染发只用 1 个（<c>JobGiver_DyeHair.cs:29 job.count = 1</c>）；
    /// 改色按"身上还有几件需要改色"取（每件消耗 1 个，<c>JobDriver_RecolorApparel.cs:64-73</c>）。</para>
    /// </summary>
    internal static class DyeFromCore
    {
        internal static bool IdeologyDyeActive => ModsConfig.IdeologyActive;

        /// <summary>地图上是否有原版能找到的染料（否则我们才搬核心的）。</summary>
        internal static bool HasUsableGroundDye(Pawn pawn)
        {
            var list = pawn.Map.listerThings.ThingsOfDef(ThingDefOf.Dye);
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].IsForbidden(pawn) && pawn.CanReserve(list[i], 1, 1)) return true;
            }
            return false;
        }

        /// <summary>有没有可用（可及、可预约、交互格可坐/可站）的造型站 —— 与 <c>JobGiver_DyeHair.cs:18</c> 同口径。</summary>
        internal static bool HasReachableStylingStation(Pawn pawn)
        {
            Thing station = GenClosest.ClosestThing_Global_Reachable(
                pawn.Position, pawn.Map, pawn.Map.listerBuildings.AllBuildingsColonistOfDef(ThingDefOf.StylingStation),
                PathEndMode.InteractionCell, TraverseParms.For(pawn), 9999f,
                x => !x.IsForbidden(pawn) && pawn.CanReserve(x));
            return station != null;
        }

        /// <summary>与 <c>JobGiver_OptimizeApparel.cs:185</c> 同口径的造型站校验（Touch + 交互格可预约）。</summary>
        internal static bool HasReachableStylingStationForRecolor(Pawn pawn)
        {
            Thing station = GenClosest.ClosestThingReachable(
                pawn.Position, pawn.Map, ThingRequest.ForDef(ThingDefOf.StylingStation), PathEndMode.Touch,
                TraverseParms.For(pawn), 9999f,
                t => !t.IsForbidden(pawn) && pawn.CanReserve(t) && pawn.CanReserveSittableOrSpot(t.InteractionCell));
            return station != null;
        }
    }

    /// <summary>① 染发：原版 <c>JobGiver_DyeHair.cs:23</c> 只扫地图。</summary>
    [HarmonyPatch(typeof(JobGiver_DyeHair), "TryGiveJob")]
    internal static class Patch_JobGiver_DyeHair_TryGiveJob
    {
        private static void Prefix(Pawn pawn)
        {
            if (pawn?.Map == null) return;
            if (!DyeFromCore.IdeologyDyeActive) return;
            if (pawn.style == null || pawn.story == null) return;

            // 原版 :14 的两条前置：没定颜色 / 颜色没变 ⇒ 根本不染发，别搬染料。
            if (!pawn.style.nextHairColor.HasValue) return;
            if (pawn.style.nextHairColor == pawn.story.HairColor) return;
            if (!ConsumePatchUtil.PawnMayUseCore(pawn)) return;

            // 地图上已有可用染料 ⇒ 原版自己会找到（本图优先，不跨图搬）。
            if (DyeFromCore.HasUsableGroundDye(pawn)) return;
            if (!DyeFromCore.HasReachableStylingStation(pawn)) return;

            PaintFromCore.MaterializeDye(pawn, 1);      // 染发只要 1 个
        }
    }

    /// <summary>② 衣物改色：原版 <c>JobGiver_OptimizeApparel.cs:197</c> 只扫地图。</summary>
    [HarmonyPatch(typeof(JobGiver_OptimizeApparel), nameof(JobGiver_OptimizeApparel.TryCreateRecolorJob))]
    internal static class Patch_JobGiver_OptimizeApparel_TryCreateRecolorJob
    {
        private static void Prefix(Pawn pawn, bool dryRun)
        {
            // 预览调用（Building_StylingStation.cs:30）绝不能有副作用。
            if (dryRun) return;
            if (pawn?.Map == null || pawn.apparel == null) return;
            if (!DyeFromCore.IdeologyDyeActive) return;
            if (!pawn.apparel.AnyApparelNeedsRecoloring) return;
            if (!ConsumePatchUtil.PawnMayUseCore(pawn)) return;
            if (DyeFromCore.HasUsableGroundDye(pawn)) return;
            if (!DyeFromCore.HasReachableStylingStationForRecolor(pawn)) return;

            int need = 0;
            var worn = pawn.apparel.WornApparel;
            for (int i = 0; i < worn.Count; i++)
            {
                if (worn[i].DesiredColor.HasValue) need++;    // 与 :192 同判据
            }
            if (need <= 0) return;

            PaintFromCore.MaterializeDye(pawn, need);
        }
    }
}
