using System;
using DigitalStorage.AI;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 「清空废料容器」的两级接管。原版这条链只认**格子**目的地，而我们的核心是**容器**。
    ///
    /// <para><b>原版为什么会坏</b>（社区反馈 2026-10-18）：</para>
    /// <code>
    /// // WorkGiver_EmptyWasteContainer.cs:36 / :51
    /// StoreUtility.TryFindBestBetterStorageFor(waste, pawn, pawn.Map, StoragePriority.Unstored, ...,
    ///                                          out var foundCell, out var _);
    /// // :56
    /// Job job = JobMaker.MakeJob(JobDefOf.EmptyWasteContainer, t, compWasteProducer.Waste, foundCell);
    /// </code>
    /// <para>目的地是容器（存储核心）时 <c>foundCell</c> 是 <c>IntVec3.Invalid</c>；而
    /// <c>JobDriver_EmptyThingContainer.TryMakePreToilReservations</c> 会去预订这个非法格子 ⇒
    /// 作业建不起来且**没有任何日志**（玩家只看到"小人永远不来清理"，垃圾把机械培育舱顶死）。</para>
    ///
    /// <para><b>两级接管</b>：</para>
    /// <list type="number">
    /// <item><b>送进核心</b>（首选）：最优目的地是「能收这份垃圾的 <see cref="Building_StorageCore"/>」时，
    ///   改用 <see cref="JobDriver_DS_EmptyWasteToCore"/> —— 开盖、把废料散到地上、抱起来送进核心
    ///   （见 <see cref="EmptyWasteFallback.TryMakeToCore"/>）。</item>
    /// <item><b>退回格子</b>（兜底）：核心不收（玩家关掉垃圾筛选 / 满了 / 断电）或地图上没核心时，
    ///   用 <c>StoreUtility.TryFindStoreCellNearColonyDesperate</c> 找一个真实格子，照原版形态重建
    ///   同一个 <c>JobDefOf.EmptyWasteContainer</c>（<see cref="EmptyWasteFallback.TryMakeCell"/>）。</item>
    /// </list>
    /// </summary>
    static class EmptyWasteFallback
    {
        /// <summary>
        /// 首选：造一个「废料 → 核心」的 job（<see cref="JobDriver_DS_EmptyWasteToCore"/>）。
        /// 目标布局 A = 废料容器、B = 核心、C = 废料。造不出来返回 null（调用方继续走下一条路）。
        /// </summary>
        internal static Job TryMakeToCore(Pawn pawn, Thing container, bool forced)
        {
            if (pawn?.Map == null || container == null) return null;
            if (container.Map != pawn.Map) return null;
            if (!ModLister.BiotechInstalled) return null;

            CompWasteProducer producer = container.TryGetComp<CompWasteProducer>();
            if (producer == null || !producer.CanEmptyNow) return null;

            Thing waste = producer.Waste;
            if (waste == null || waste.stackCount <= 0) return null;
            // 只管「有害垃圾」这一族：别的容器内容物（if any）维持原版行为
            if (!Core.WasteUtil.IsHazardousWaste(waste)) return null;

            // 原版 HasJobOnThing 的第一道门（工作类型/势力由 WorkGiver 层负责，这里只补预订）
            if (!pawn.CanReserve(container, 1, -1, ReservationLayerDefOf.Empty, forced)) return null;

            // 跟原版问同一个问题：这份垃圾的**最优**目的地是谁？
            // 只有最优是核心才接管 —— 最优是储存区格子时原版自己走得通，别多事。
            if (!StoreUtility.TryFindBestBetterStorageFor(waste, pawn, pawn.Map, StoragePriority.Unstored,
                    pawn.Faction, out IntVec3 _, out IHaulDestination dest, needAccurateResult: false))
                return null;

            Building_StorageCore core = dest as Building_StorageCore;
            if (core == null || core.Destroyed || core.Map != pawn.Map) return null;
            if (core.Faction != null && core.Faction != pawn.Faction) return null;
            if (core.IsForbidden(pawn)) return null;
            // Accepts = 入库开关 + 电力 + 筛选 + 容量（见 Building_StorageCore.Accepts）。
            // 玩家把垃圾从筛选里关掉 ⇒ 这里 false ⇒ 落回原版/格子兜底。
            if (!core.Accepts(waste)) return null;
            if (!pawn.CanReach(core, PathEndMode.Touch, Danger.Deadly)) return null;

            Job job = JobMaker.MakeJob(DigitalStorage_JobDefOf.DigitalStorage_EmptyWasteToCore, container, core, waste);
            job.count = waste.stackCount;
            return job;
        }

        /// <summary>
        /// 兜底：照 <c>WorkGiver_EmptyWasteContainer.JobOnThing</c> 的形态造一个**目的地是格子**的
        /// <c>EmptyWasteContainer</c>；造不出来返回 null（调用方保持原版结果）。
        /// </summary>
        internal static Job TryMakeCell(Pawn pawn, Thing container, bool forced)
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

    /// <summary><c>HasJobOnThing</c>：原版说没去处时，用两条兜底路的存在性重判（与 JobOnThing 保持一致）。</summary>
    [HarmonyPatch(typeof(WorkGiver_EmptyWasteContainer), "HasJobOnThing")]
    static class Patch_EmptyWasteContainerFallback_HasJob
    {
        static void Postfix(Pawn pawn, Thing t, bool forced, ref bool __result)
        {
            if (__result) return;
            try
            {
                if (EmptyWasteFallback.TryMakeToCore(pawn, t, forced) != null
                    || EmptyWasteFallback.TryMakeCell(pawn, t, forced) != null)
                {
                    __result = true;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 清空废料容器兜底判定失败（本次仍按原版处理）：" + e, 0x44534411);
            }
        }
    }

    /// <summary>
    /// <c>JobOnThing</c>：三级介入 —— ① 最优目的地是核心 ⇒ 换成「送进核心」的 job；
    /// ② 原版返回 null 或 <c>targetC</c> 非法（目的地是容器 ⇒ driver 预订必败）⇒ 换成格子目的地的 job。
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_EmptyWasteContainer), "JobOnThing")]
    static class Patch_EmptyWasteContainerFallback_Job
    {
        static void Postfix(Pawn pawn, Thing t, bool forced, ref Job __result)
        {
            try
            {
                Job toCore = EmptyWasteFallback.TryMakeToCore(pawn, t, forced);
                if (toCore != null)
                {
                    __result = toCore;
                    return;
                }

                if (__result != null && __result.targetC.IsValid) return;

                Job fallback = EmptyWasteFallback.TryMakeCell(pawn, t, forced);
                if (fallback != null)
                {
                    __result = fallback;
                    return;
                }

                // 原版给了一个「目的地是容器 ⇒ targetC 非法」的作业，而我们连格子都找不到：
                // 与其让 TryMakePreToilReservations 在非法格子上静默失败（玩家只看到小人发呆），
                // 不如干脆不给作业 —— WorkGiver 层会按"没有可放的地方"正常跳过。
                if (__result != null && !__result.targetC.IsValid) __result = null;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 清空废料容器兜底建 job 失败（本次仍按原版处理）：" + e, 0x44534412);
            }
        }
    }
}
