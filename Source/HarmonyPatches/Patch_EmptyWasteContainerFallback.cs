using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 「清空废料容器」的兜底：原版作业只支持**格子**目的地。
    ///
    /// <para><b>为什么需要</b>（社区反馈 2026-10-18 第 1 条）：</para>
    /// <code>
    /// // WorkGiver_EmptyWasteContainer.cs:36 / :51
    /// StoreUtility.TryFindBestBetterStorageFor(waste, pawn, pawn.Map, StoragePriority.Unstored, ...,
    ///                                          out var foundCell, out var _);
    /// // :56
    /// Job job = JobMaker.MakeJob(JobDefOf.EmptyWasteContainer, t, compWasteProducer.Waste, foundCell);
    /// </code>
    /// <para>目的地是**容器**（例如优先级为 Preferred 的存储核心）时，<c>foundCell</c> 是
    /// <c>IntVec3.Invalid</c>；而 <c>JobDriver_EmptyThingContainer.TryMakePreToilReservations</c>
    /// 会 <c>pawn.Reserve(job.GetTarget(TargetIndex.C), ...)</c> —— 预订非法格子必然失败
    /// ⇒ 作业建不起来，而且**没有任何日志**（玩家只看到"小人永远不来清理"，充电桩满了之后
    /// 机械体连电都充不上）。这条 driver 的收尾是 <c>CarryHauledThingToCell</c> +
    /// <c>PlaceHauledThingInCell</c>，天生不支持"塞进任意容器"。</para>
    ///
    /// <para><b>兜底做法</b>：只在原版"根本没有可行目的地"或"目的地是容器（targetC 非法）"
    /// 这两种情况下介入 —— 用 <c>StoreUtility.TryFindStoreCellNearColonyDesperate</c>
    /// 找一个**真实格子**（先找任意可放格子，再退到殖民地内空地/殖民地外），照原版形态
    /// 重建同一个 job。废料落地后由自动收纳或小人继续处理；找不到格子就维持原版行为。</para>
    ///
    /// <para><b>顺带的好处</b>：核心默认筛选已经关掉有害垃圾（<c>Core.WasteUtil</c>），
    /// 正常情况下原版"格子腿"自己就通了，这个兜底只在玩家手动让核心收垃圾、
    /// 或殖民地里一格垃圾储存区都没有时才用得上。</para>
    /// </summary>
    static class EmptyWasteFallback
    {
        /// <summary>
        /// 照 <c>WorkGiver_EmptyWasteContainer.JobOnThing</c> 的形态造一个**目的地是格子**的 job；
        /// 造不出来返回 null（调用方保持原版结果）。
        /// </summary>
        internal static Job TryMake(Pawn pawn, Thing container, bool forced)
        {
            if (pawn?.Map == null || container == null) return null;
            if (container.Map != pawn.Map) return null;
            if (!ModLister.BiotechInstalled) return null;

            CompWasteProducer producer = container.TryGetComp<CompWasteProducer>();
            if (producer == null || !producer.CanEmptyNow) return null;

            Thing waste = producer.Waste;
            if (waste == null || waste.stackCount <= 0) return null;

            // 原版 HasJobOnThing 的第一道门（工作类型/势力由 WorkGiver 层负责，这里只补预订）
            if (!pawn.CanReserve(container, 1, -1, ReservationLayerDefOf.Empty, forced)) return null;

            if (!StoreUtility.TryFindStoreCellNearColonyDesperate(waste, pawn, out IntVec3 cell))
                return null;
            if (!cell.IsValid) return null;

            Job job = JobMaker.MakeJob(JobDefOf.EmptyWasteContainer, container, waste, cell);
            job.count = waste.stackCount;
            return job;
        }
    }

    /// <summary><c>HasJobOnThing</c>：原版说没去处时，用兜底 job 的存在性重判（与 JobOnThing 保持一致）。</summary>
    [HarmonyPatch(typeof(WorkGiver_EmptyWasteContainer), "HasJobOnThing")]
    static class Patch_EmptyWasteContainerFallback_HasJob
    {
        static void Postfix(Pawn pawn, Thing t, bool forced, ref bool __result)
        {
            if (__result) return;
            try
            {
                if (EmptyWasteFallback.TryMake(pawn, t, forced) != null) __result = true;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 清空废料容器兜底判定失败（本次仍按原版处理）：" + e, 0x44534411);
            }
        }
    }

    /// <summary>
    /// <c>JobOnThing</c>：两种介入点 —— ① 原版返回 null（没有目的地）；② 原版返回了 job，
    /// 但 <c>targetC</c> 非法（目的地是容器 ⇒ driver 预订必败）。都换成格子目的地的 job。
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_EmptyWasteContainer), "JobOnThing")]
    static class Patch_EmptyWasteContainerFallback_Job
    {
        static void Postfix(Pawn pawn, Thing t, bool forced, ref Job __result)
        {
            try
            {
                if (__result != null && __result.targetC.IsValid) return;

                Job fallback = EmptyWasteFallback.TryMake(pawn, t, forced);
                if (fallback != null) __result = fallback;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 清空废料容器兜底建 job 失败（本次仍按原版处理）：" + e, 0x44534412);
            }
        }
    }
}
