using RimWorld;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 本 mod 自己的 JobDef 索引。
    ///
    /// <para><b>4.0 批 2 起只剩三条</b> —— 另外两条已删，因为原版自己就能做，且都已实测通过：
    /// <list type="bullet">
    /// <item><c>DigitalStorage_IngestToCore</c>（入库）→ 原生
    ///   <c>StoreUtility.TryFindBestBetterNonSlotGroupStorageFor</c> 认 <c>IHaulDestination</c>
    ///   + <c>HaulAIUtility.HaulToStorageJob</c> 走 <c>HaulToContainerJob</c></item>
    /// <item><c>DigitalStorage_WithdrawToBill</c>（bill 取料）→ 原生
    ///   <c>WorkGiver_DoBill.cs:481</c> 遍历 <c>AllHaulSourcesListForReading</c>
    ///   + <c>JobDriver_DoBill.cs:121</c> 的 <c>canGotoSpawnedParent: true</c></item>
    /// </list></para>
    ///
    /// <para>留下这三条的共同点：<b>原版那条 job 链里没有"目标可以住在容器里"这个假设</b>
    /// （没有 <c>canGotoSpawnedParent</c>，或压根不是 worker job），
    /// 所以只能自己把东西取出来交给原版。</para>
    /// </summary>
    [DefOf]
    public static class DigitalStorage_JobDefOf
    {
        /// <summary>建造投料：从容器取料到蓝图 / Frame。原版 <c>GenClosest</c> 没传 <c>lookInHaulSources</c>。</summary>
        public static JobDef DigitalStorage_WithdrawToConstruction;

        /// <summary>右键「取出到指定格」。</summary>
        public static JobDef DigitalStorage_WithdrawToSpot;

        /// <summary>取消耗品直接进嘴（原版吃 / 用药的 job 链不接容器）。
        /// 4.0：defName 由 <c>DigitalStorage_ConsumeFromLedger</c> 改为 <c>DigitalStorage_Consume</c>
        /// —— 账本已整体删除，"FromLedger" 这个名字不再成立。</summary>
        public static JobDef DigitalStorage_Consume;

        /// <summary>
        /// 清空废料容器 → 废料直接送进存储核心（2026-10-18 社区反馈）。
        /// 原版 <c>JobDriver_EmptyThingContainer</c> 只支持格子目的地：容器目的地会让
        /// <c>targetC</c> 变成 <c>IntVec3.Invalid</c> ⇒ 作业静默建不起来。
        /// 由 <c>Patch_EmptyWasteContainerFallback</c> 在「最优目的地是核心」时换成这条。
        /// </summary>
        public static JobDef DigitalStorage_EmptyWasteToCore;

        static DigitalStorage_JobDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(DigitalStorage_JobDefOf));
        }
    }
}
