using DigitalStorage.Components;
using DigitalStorage.Settings;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>让「预约核心」不再是独占名额</b> —— 多个小人（以及各种搬运 mod）可以同时把核心当作
    /// 目的地/来源预约下来。
    ///
    /// <para><b>为什么原版只放一个</b>：<c>Verse.AI/ReservationManager.cs</c> 的
    /// <c>CanReserve(claimant, target, maxPawns = 1, stackCount = -1, …)</c> 对**一切**目标
    /// 都按「目标能分成几份」来算名额：
    /// <code>
    /// int num = target.HasThing ? target.Thing.stackCount : 1;   // ← 建筑 = 1
    /// int num2 = stackCount == -1 ? num : stackCount;
    /// …
    /// if (num3 &gt;= maxPawns || num2 + num4 &gt; num) return false;
    /// </code>
    /// 建筑 <c>stackCount</c> 恒为 1 ⇒ 无论调用方传 <c>maxPawns</c> 多少，
    /// 第二个预约者的 <c>num2 + num4 &gt; num</c> 必然成立，一定订不到。
    /// 核心是个 3×3 的大容器，却和「往工作台里塞一份材料」共用同一条规则，
    /// 于是同一时刻只有一个搬运工/使用者能"占住"它：其余人开局就停在「目标已被预订」。</para>
    ///
    /// <para><b>两边其实都不需要独占</b>：
    /// 入库是 <c>Toils_Haul.DepositHauledThingInContainer</c> 里一次
    /// <c>innerContainer.TryTransferToContainer(…)</c>，取料是
    /// <c>JobDriver_DS_Withdraw/DS_Consume</c> 在自己的 toil 里直接搬，
    /// 两边都是**同一帧内的原子写**，不靠预约互斥；预约在这里唯一的作用是
    /// 「告诉别人我要用」，而一个 3×3 容器本来就允许多人同时用。</para>
    ///
    /// <para><b>只对我们自己的核心生效</b>（<c>target.Thing is Building_StorageCore</c>）：
    /// 别家容器/货架/装载舱的预约语义各有用途（在途统计、装载取消、物理交互……），一律不动。
    /// 也**保留**原版所有前置校验（claimant 已 spawn 同图、目标有效未销毁、同图），
    /// 只跳过「名额」这一段 —— 跨图预约那种会写坏存档的路径依旧被原版拒。</para>
    ///
    /// <para><b>热路径</b>：第一道就是 <c>target.Thing is Building_StorageCore</c> 的类型判断，
    /// 不是核心就 <c>return true</c> 走原版，零额外开销。</para>
    ///
    /// <para>开关沿用 <see cref="DigitalStorageSettings.concurrentCoreDelivery"/>（默认开）——
    /// 它本来就是「允许多个小人同时用核心」的总闸，关掉即完全回到原版语义。</para>
    /// </summary>
    [HarmonyPatch(typeof(ReservationManager), nameof(ReservationManager.CanReserve),
        new[] { typeof(Pawn), typeof(LocalTargetInfo), typeof(int), typeof(int), typeof(ReservationLayerDef), typeof(bool) })]
    internal static class Patch_ReservationManager_CanReserve_SharedCore
    {
        private static bool Prefix(Pawn claimant, LocalTargetInfo target, ref bool __result)
        {
            if (!DigitalStorageSettings.concurrentCoreDelivery) return true;

            if (!(target.Thing is Building_StorageCore core)) return true;

            // 原版这几道照旧（它们防的是"订到一个根本用不了的东西"和跨图脏预约）
            if (claimant == null || !claimant.Spawned) return true;
            if (claimant.Map == null || claimant.Map != core.Map) return true;
            if (!core.Spawned || core.Destroyed) return true;
            if (target.ThingDestroyed) return true;

            __result = true;
            return false;
        }
    }

    /// <summary>
    /// 同上，补 <c>CanReserveStack</c>：它给「这条路上我能订到多少」的答案，
    /// 对建筑按原版公式只会给出 0（<c>num - num2</c>，被别的预约者吃光）。
    /// 核心按「一个建筑算一份」回答，保证 <c>ReserveAsManyAsPossible</c> 之类的调用方
    /// 也能拿到一致的结果。
    /// </summary>
    [HarmonyPatch(typeof(ReservationManager), nameof(ReservationManager.CanReserveStack),
        new[] { typeof(Pawn), typeof(LocalTargetInfo), typeof(int), typeof(ReservationLayerDef), typeof(bool) })]
    internal static class Patch_ReservationManager_CanReserveStack_SharedCore
    {
        private static bool Prefix(Pawn claimant, LocalTargetInfo target, ref int __result)
        {
            if (!DigitalStorageSettings.concurrentCoreDelivery) return true;

            if (!(target.Thing is Building_StorageCore core)) return true;
            if (claimant == null || !claimant.Spawned) return true;
            if (claimant.Map == null || claimant.Map != core.Map) return true;
            if (!core.Spawned || core.Destroyed) return true;
            if (target.ThingDestroyed) return true;

            __result = target.Thing.stackCount;
            return false;
        }
    }
}
