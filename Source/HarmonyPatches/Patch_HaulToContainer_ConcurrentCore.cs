using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>让多个小人同时往同一个核心送货</b>（社区反馈第 4 条：「物品很远时只有一个小人能搬，
    /// 其他小人用不了核心」）。
    ///
    /// <para><b>为什么原版只放一个人</b>：<see cref="JobDriver_HaulToContainer.TryMakePreToilReservations"/> 里有
    /// <c>pawn.Reserve(job.GetTarget(TargetIndex.B), job, 1, 1, null, errorOnFailed)</c> ——
    /// 容器（<c>targetB</c>）是**单名额预约**。核心是个 3×3 的大容器，却和"往工作台里塞一份材料"
    /// 共用同一条规则，于是同一时刻只允许一个搬运工把东西送进去：其余人一开局就停在
    /// "目标已被预订"，表现为**只有一个小人在搬**。</para>
    ///
    /// <para><b>为什么可以放开</b>：真正的入库动作是
    /// <see cref="Toils_Haul.DepositHauledThingInContainer"/> 里的一次
    /// <c>innerContainer.TryTransferToContainer(...)</c> —— <b>它不需要预约容器</b>，
    /// 转移本身是原子的（同一帧只有一个 pawn 在写 ThingOwner）。
    /// 放下动作走到的是 <c>PathEndMode.Touch</c>（贴着 3×3 核心的任意一格都行），
    /// 不存在"只有一个交互格"的瓶颈。全程还有 <c>FailOn</c> 兜底：
    /// 容器满了 / 不再接受这类物品 ⇒ 作业失败，物品留在小人身上，下一轮重新规划。</para>
    ///
    /// <para><b>只对我们自己的核心生效</b>：判据是 <c>targetB.Thing is Building_StorageCore</c>。
    /// 别家的容器、原版货架、传送装载舱（<see cref="IHaulEnroute"/>/<see cref="ILoadingDestination"/>）
    /// 一律走原版逻辑 —— 那些容器的预约语义有自己的用途（在途统计、装载取消等）。</para>
    ///
    /// <para>开关 <c>concurrentCoreDelivery</c> 默认开，关掉即完全回到原版。</para>
    /// </summary>
    [HarmonyPatch(typeof(JobDriver_HaulToContainer), nameof(JobDriver_HaulToContainer.TryMakePreToilReservations))]
    internal static class Patch_HaulToContainer_ConcurrentCore
    {
        private static bool Prefix(JobDriver_HaulToContainer __instance, bool errorOnFailed, ref bool __result)
        {
            if (!DigitalStorage.Settings.DigitalStorageSettings.concurrentCoreDelivery) return true;

            Job job = __instance.job;
            if (job == null) return true;
            if (!(job.GetTarget(TargetIndex.B).Thing is Building_StorageCore)) return true;

            Pawn pawn = __instance.pawn;
            if (pawn == null) return true;

            // ① 货物本身照旧预约（这一个不能省：两个小人不能抬同一堆）
            if (!pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, -1, null, errorOnFailed))
            {
                __result = false;
                return false;
            }

            // ② 目标队列照旧
            pawn.ReserveAsManyAsPossible(job.GetTargetQueue(TargetIndex.A), job);

            // ③ 刻意**不预约 targetB**：核心允许多人同时送货，入库靠 TryTransferToContainer 的原子性。
            //    也刻意不调原版的 UpdateEnrouteTrackers()——它只对 IHaulEnroute 容器有意义，核心不是。

            __result = true;
            return false;
        }
    }
}
