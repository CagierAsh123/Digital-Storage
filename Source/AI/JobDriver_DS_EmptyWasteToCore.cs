using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 「清空废料容器」的**容器目的地**版：把废料容器（机械培育舱 / 失能机关的机械组装平台等）
    /// 里的有毒垃圾直接送进<b>数字存储核心</b>。
    ///
    /// <para><b>为什么不能复用原版</b>（社区反馈 2026-10-18）：</para>
    /// <code>
    /// // WorkGiver_EmptyWasteContainer.JobOnThing:51-58
    /// StoreUtility.TryFindBestBetterStorageFor(waste, pawn, map, StoragePriority.Unstored, ...,
    ///                                          out IntVec3 foundCell, out IHaulDestination _);
    /// JobMaker.MakeJob(JobDefOf.EmptyWasteContainer, t, compWasteProducer.Waste, foundCell);
    /// </code>
    /// <para>目的地是<b>容器</b>（我们的核心、其它 IHaulDestination 容器）时 <c>foundCell</c> 恒为
    /// <c>IntVec3.Invalid</c>；而 <c>JobDriver_EmptyThingContainer.TryMakePreToilReservations</c>
    /// 会去预订 <c>targetC</c> 这个非法格子 ⇒ 必然失败、且**没有任何日志**。那条 driver 的收尾是
    /// <c>CarryHauledThingToCell</c> + <c>PlaceHauledThingInCell</c>，天生只会"放到格子上"。</para>
    ///
    /// <para><b>本 driver 的形态</b>：前半段照抄原版（走过去 → 等 120 tick 开盖 → 把容器内容物散到
    /// 脚下），后半段换成"抱起来 → 走到容器 → <c>DepositHauledThingInContainer</c>"。
    /// 目标布局刻意排成 <b>A = 废料容器、B = 核心、C = 废料</b>：
    /// <list type="bullet">
    /// <item><c>Toils_General.WaitWhileExtractingContents(A, C, 120)</c> 干活；</item>
    /// <item><c>Toils_Haul.CarryHauledThingToContainer()</c> 写死用 <c>targetB.Thing</c> 当容器
    ///   （见 <c>Verse.AI\Toils_Haul.cs:388-407</c>）⇒ B 必须是核心；</item>
    /// <item><c>Toils_Haul.DepositHauledThingInContainer(B, None)</c> 把手上那份塞进
    ///   <c>B.TryGetInnerInteractableThingOwner()</c>，也就是核心的 <c>innerContainer</c>
    ///   —— 顺便走 <c>CoreStorageContainer.TryAdd</c> 的筛选/开关判据。</item>
    /// </list></para>
    ///
    /// <para><b>塞不进去怎么办</b>：核心满/断电/玩家把垃圾从筛选里关掉，都会让
    /// <c>CarryHauledThingToContainer</c> 的 fail 条件或 <c>DepositHauledThingInContainer</c> 的
    /// 转移数量变成 0。前者让作业干净结束（<c>JobCondition.Incompletable</c>），后者由末尾的
    /// 收尾 toil 就地把剩下的放下 —— 绝不让人抱着垃圾卡在作业里。</para>
    /// </summary>
    public class JobDriver_DS_EmptyWasteToCore : JobDriver
    {
        /// <summary>要清空的废料容器（机械培育舱 / 机械组装平台）。</summary>
        private const TargetIndex ContainerInd = TargetIndex.A;

        /// <summary>目的地：数字存储核心。</summary>
        private const TargetIndex CoreInd = TargetIndex.B;

        /// <summary>容器里的那份废料（散到地上之后才被预订/抱起）。</summary>
        private const TargetIndex WasteInd = TargetIndex.C;

        private const int OpenTicks = 120;

        private Thing Container => job.GetTarget(ContainerInd).Thing;

        private Building_StorageCore TargetCore => job.GetTarget(CoreInd).Thing as Building_StorageCore;

        private Thing TargetWaste => job.GetTarget(WasteInd).Thing;

        /// <summary>原版同一套：有交互格就从交互格去，没有就贴上去（见 <c>JobDriver_EmptyThingContainer.ContainerPathEndMode</c>）。</summary>
        private PathEndMode ContainerPathEndMode
        {
            get
            {
                Thing container = Container;
                if (container?.def == null || !container.def.hasInteractionCell) return PathEndMode.Touch;
                return PathEndMode.InteractionCell;
            }
        }

        /// <summary>
        /// 只预订废料容器本体（层 = <c>ReservationLayerDefOf.Empty</c>，与
        /// <c>WorkGiver_EmptyWasteContainer.GetReservationLayer</c> 同一个层 —— JobDef 里也必须
        /// 写 <c>containerReservationLayer = Empty</c>，否则同一个容器会被两个小人各清一次）。
        ///
        /// <para>核心**不预订**：它是并发目的地（<c>Patch_ReservationManager_CanReserve_SharedCore</c>），
        /// 预订反而会把别人挡在外面；塞不进去的兜底见类注释。</para>
        /// </summary>
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (!pawn.Reserve(Container, job, 1, -1, ReservationLayerDefOf.Empty, errorOnFailed))
                return false;
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // ---- 前半段：照原版「开容器、把内容物散到脚下」 ----
            yield return Toils_Goto.GotoThing(ContainerInd, ContainerPathEndMode)
                .FailOnDespawnedNullOrForbidden(ContainerInd)
                .FailOnSomeonePhysicallyInteracting(ContainerInd)
                .FailOn(() =>
                {
                    // 容器中途被别人清空了 ⇒ 没意义（原版同名判断，防止打开一个空舱）
                    Thing t = Container;
                    return t != null && t.TryGetComp(out CompThingContainer comp) && comp.Empty;
                });

            yield return Toils_General.WaitWhileExtractingContents(ContainerInd, WasteInd, OpenTicks);

            yield return Toils_General.Do(() =>
            {
                Thing container = Container;
                if (container == null) return;
                if (container.TryGetInnerInteractableThingOwner().TryDropAll(pawn.Position, pawn.Map, ThingPlaceMode.Near))
                {
                    container.TryGetComp<CompThingContainer>()?.Props.dropEffecterDef?.Spawn(container, Map).Cleanup();
                }
            });

            // ---- 后半段：抱起废料 → 送进核心 ----
            yield return Toils_Reserve.Reserve(WasteInd);

            yield return Toils_Goto.GotoThing(WasteInd, PathEndMode.Touch)
                .FailOnDespawnedNullOrForbidden(WasteInd)
                .FailOnSomeonePhysicallyInteracting(WasteInd);

            yield return Toils_Haul.StartCarryThing(WasteInd, putRemainderInQueue: false, subtractNumTakenFromJobCount: true);

            // targetB = 核心：这条原版 toil 写死了「把手上那件送进 targetB.Thing」
            yield return Toils_Haul.CarryHauledThingToContainer();

            yield return Toils_Haul.DepositHauledThingInContainer(CoreInd, TargetIndex.None);

            // ---- 收尾：只塞进去一部分（核心快满）时，把剩下的就地放下 ----
            yield return Toils_General.Do(() =>
            {
                if (pawn?.carryTracker?.CarriedThing == null) return;
                pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
            });
        }
    }
}
