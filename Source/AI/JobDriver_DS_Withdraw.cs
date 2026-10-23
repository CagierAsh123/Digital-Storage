using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 阶段 4.3+：通用"出核心"方向 JobDriver（非 Bill 场景）。
    /// 每趟 Job 取**一件真实的 Thing**，搬到 targetA 放下。
    ///
    /// <para><b>4.0 改造</b>：要取的那件东西直接是 <c>targetB</c>（真实 Thing），
    /// 不再有 <c>ItemKey</c>、不再有挂在 job 上的计划字典
    /// （<c>JobDriver_DS_ReserveHelper</c> 已删）。理由同消耗链：
    /// 「全放开」后同一 def 可能有多把传奇剑，<c>(def,stuff)</c> 指不出是哪一把；
    /// 而 job 目标会被 Scribe，存档读档不丢。</para>
    ///
    /// <para><b>纯轮椅</b>：不再有代理点走位 —— 3.0 的 <c>targetB</c> 是"代理格"，
    /// 无芯片的 pawn 要先走过去；现在 <c>targetB</c> 改指「要取的那件东西」，
    /// 材料直接到手（<c>CoreFinder.PickProxyCell</c> / 代理点机制已整体删除）。</para>
    ///
    /// targetA = 目的地（蓝图 / Frame / 玩家指定格）
    /// targetB = 要取的那件东西（住在容器里）
    /// targetC = 它所在的容器建筑（用于通电/存在性检查）
    /// job.count = 取多少
    /// </summary>
    public class JobDriver_DS_Withdraw : JobDriver
    {
        /// <summary>要取的那件东西。</summary>
        private Thing SourceThing => job.GetTarget(TargetIndex.B).Thing;

        /// <summary>它所在的容器（取出前有效）。</summary>
        private Building_StorageCore TargetCore => job.GetTarget(TargetIndex.C).Thing as Building_StorageCore;

        /// <summary>
        /// <b>⚠️ 这个方法绝不能返回 false。</b>
        ///
        /// <para><c>Pawn_JobTracker.StartJob</c>（<c>:377-381</c>）对"非排队 job 在 StartJob 之后
        /// 预订失败"只有一句 <c>Log.Warning("TryMakePreToilReservations() returned false ... This should
        /// have been checked before.")</c> + <c>EndCurrentJob(JobCondition.Errored)</c>，
        /// 而 <c>Errored</c> 会让原版挂一个 <c>Wait</c> 恢复作业
        /// —— 那就是用户实测的「<b>完成建造后进入几秒等待 job</b>」（2026-10-02）。</para>
        ///
        /// <para>所以这里只<b>尽力订</b>（<c>errorOnFailed: false</c>），订不到交给 toil 干净地判
        /// <c>Incompletable</c>：取料 toil 本来就有 <c>owner.Contains</c> + <c>SplitOff</c> 兜底，
        /// 目的地那一条由 <see cref="GuardDest"/> 顶住。真正该不该开这个作业，由
        /// <c>WorkGiver_DS_WithdrawForConstruct</c> 的 <c>CanReserve</c> /
        /// <c>ConstructMaterialPlanner</c> 的预订筛选负责 —— 那才是"should have been checked before"
        /// 说的地方。</para>
        /// </summary>
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            // 【B6 / 审计 2026-10-17】现场规划必须在这里做，不能等 Notify_Starting。
            //
            // Pawn_JobTracker.StartJob 的顺序是 `:336 TryMakePreToilReservations` → `:368 Notify_Starting`，
            // 也就是说：规划如果留在 Notify_Starting，那么这一趟**预订时** job.targetB/C 还是空的、
            // job.count 还是 0 ⇒ 源料堆一件都没订上（`reserveCount` 落到 -1、`src == null` 直接跳过），
            // 同一堆料可以被两个小人在同一帧各订一份，第二个到现场才发现被掏空。
            // 规划提前后，下面按真实数量订源堆，多个小人仍然各订同一大堆的一部分（累加语义）。
            EnsurePlanned();

            // 按**本次要取的数量**订，而不是整堆：ReservationManager 会把同目标上别人已订的
            // stackCount 累加后比较 ⇒ 多个 pawn 能各订同一大堆的一部分，工地吞吐不会退化成串行。
            int reserveCount = job.count > 0 ? job.count : -1;

            Thing dest = job.GetTarget(TargetIndex.A).Thing;
            if (dest != null && !dest.Destroyed) pawn.Reserve(dest, job, 1, -1, null, false);

            // 【跨图】别的图核心里的东西，原版 ReservationManager.CanReserve 的地图门
            // （`target.Thing.MapHeld != map`）一定拒 —— 那边由 owner.Contains 兜底，不预订。
            Thing src = SourceThing;
            if (src != null && !src.Destroyed && src.MapHeld == pawn.Map)
                pawn.Reserve(src, job, 1, reserveCount, null, false);

            // 【4.1 / 用户 2026-10-23 报的"多人同建一个框，材料超发最后凭空消失"】
            // 登记"我这趟在途多少"到原版 EnrouteManager —— 见 RegisterEnroute 的注释。
            RegisterEnroute();

            return true;
        }

        /// <summary>
        /// 把「本趟要送多少」登记进原版 <c>EnrouteManager</c>。
        ///
        /// <para><b>为什么非登记不可</b>：<c>Frame</c> / <c>Blueprint_Build</c> 都实现
        /// <c>IHaulEnroute</c>，原版算"还差多少材料"走
        /// <c>EnrouteUtility.GetSpaceRemainingWithEnroute</c> =
        /// <c>SpaceRemainingFor(def) - EnrouteManager.GetEnroute(...)</c>。
        /// 我们这条出核心的取料链 4.0 之前**一处都没登记**（全库 0 处 <c>AddEnroute</c>），
        /// 于是每个小人规划时看到的"还差多少"都是**全额**：
        /// 三名小人同建一个缺 20 钢的框 ⇒ 各取 20、各塞进
        /// <c>Frame.resourceContainer</c>（<c>maxStacks = 999999</c>，来者不拒）⇒ 送进去 60。
        /// 而 <c>Frame.CompleteConstruction</c>（<c>Frame.cs:262-277</c>）里有一句
        /// <c>resourceContainer.ClearAndDestroyContents();</c> ⇒ <b>多出来的 40 直接销毁</b>，
        /// 玩家看到的就是"材料送进去了、建完了、仓库里少了 40"。</para>
        ///
        /// <para><b>照抄原版</b> <c>JobDriver_HaulToContainer.UpdateTracker</c>
        /// （<c>:204-227</c>）：<c>num = Mathf.Min(job.count, container.GetSpaceRemainingWithEnroute(def))</c>
        /// （**不排除自己**，因为此时自己还没登记过），然后 <c>AddEnroute</c>。
        /// 蓝图 → 框那一步不用管：<c>Blueprint_Build.MakeSolidThing</c>（<c>:70-79</c>）里有
        /// <c>base.Map.enrouteManager.SendReservations(this, frame)</c>，原版自己会把账搬过去。</para>
        ///
        /// <para><b>释放</b>：<see cref="ReleaseEnroute"/> 挂在 <c>AddFinishAction</c> 上。
        /// 容器 despawn 时原版也会清（<c>Thing.DeSpawn</c> → <c>Notify_ContainerDespawned</c>），
        /// 两边都放一次不会重复扣（<c>ReleaseFor</c> 只删这名 pawn 的条目）。
        /// 驱动实例上的这几个字段**不进存档**：读档后 job 直接续跑、不会重跑
        /// <c>TryMakePreToilReservations</c>，与存档里的 enroute 账对得上（原版同理）。</para>
        /// </summary>
        private void RegisterEnroute()
        {
            Thing src = SourceThing;
            Thing dest = job.GetTarget(TargetIndex.A).Thing;
            if (src == null || src.Destroyed || src.def == null) return;
            if (dest == null || dest.Destroyed) return;

            IHaulEnroute enroute = dest as IHaulEnroute;
            if (enroute == null) return;
            Map map = enroute.Map;
            if (map == null) return;

            int count = Math.Min(job.count, enroute.GetSpaceRemainingWithEnroute(src.def));
            if (count <= 0) return;

            map.enrouteManager.AddEnroute(enroute, pawn, src.def, count);
            TrackEnroute(enroute);
        }

        /// <summary>本趟登记过在途量的容器（收尾时逐个还回去）。</summary>
        private readonly List<IHaulEnroute> enrouteContainers = new List<IHaulEnroute>();

        private void TrackEnroute(IHaulEnroute c)
        {
            if (c == null) return;
            for (int i = 0; i < enrouteContainers.Count; i++)
                if (ReferenceEquals(enrouteContainers[i], c)) return;
            enrouteContainers.Add(c);
        }

        /// <summary>
        /// 还掉本趟登记的在途量（作业无论怎么结束都会走到）。
        /// 蓝图 → 框之后 <c>job.targetA</c> 已经是框，所以开头登记的那个蓝图实例
        /// 也一起还 —— 两个都放一次是安全的（<c>ReleaseFor</c> 只删自己的条目）。
        /// </summary>
        private void ReleaseEnroute()
        {
            // 1) 登记时那个容器
            for (int i = 0; i < enrouteContainers.Count; i++)
                ReleaseOne(enrouteContainers[i]);
            enrouteContainers.Clear();

            // 2) 当前目的地（可能已经被 MakeSolidThing 换成 Frame，账也被 SendReservations 搬过去了）
            ReleaseOne(job.GetTarget(TargetIndex.A).Thing as IHaulEnroute);
        }

        private void ReleaseOne(IHaulEnroute enroute)
        {
            if (enroute == null) return;
            Map map = enroute.Map;
            if (map == null) return;
            map.enrouteManager.ReleaseFor(enroute, pawn);
        }

        public override void Notify_Starting()
        {
            base.Notify_Starting();

            // 正常情况下 TryMakePreToilReservations 里已经规划好了；这里只兜底 + 判死。
            EnsurePlanned();

            if (job.count <= 0 || SourceThing == null) EndJobWith(JobCondition.Incompletable);
        }

        /// <summary>
        /// 把"蓝图 / Frame → 核心里的哪种材料、取多少"填进 job（幂等）。
        ///
        /// 只填不判死：失败时保持未规划，交给 <c>FailOn(SourceThing == null)</c> 或
        /// <see cref="Notify_Starting"/> 干净退出 —— 这是 <see cref="TryMakePreToilReservations"/>
        /// 不能返回 false 的代价（见上面那段说明）。
        /// </summary>
        private void EnsurePlanned()
        {
            // 右键路径：调用方已经把 Thing + count 放进 job 了
            if (job.count > 0 && SourceThing != null) return;

            Thing target = job.GetTarget(TargetIndex.A).Thing;
            IConstructible constructible = target as IConstructible;
            if (constructible == null) return;

            var plan = ConstructMaterialPlanner.TryPlan(constructible, pawn, job.playerForced, pawn.Map);
            if (plan == null) return;

            Thing src = plan.Value.thing;
            job.SetTarget(TargetIndex.B, src);
            job.SetTarget(TargetIndex.C, src.ParentHolder as Thing);
            job.count = plan.Value.count;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // 收尾时把自己登记的在途量还回去（见 RegisterEnroute）。
            AddFinishAction(_ => OnFinish());

            // 那件东西没了 / 被销毁 → 放弃
            this.FailOn(() =>
            {
                Thing src = SourceThing;
                return src == null || src.Destroyed;
            });

            // 已取出的材料不再依赖原核心供电
            this.FailOn(() =>
            {
                Building_StorageCore core = TargetCore;
                return core != null && ReferenceEquals(SourceThing?.ParentHolder, core) && !core.IsUsableNow;
            });

            // 0) 目的地被别人订走了 → **干净地退出**（Incompletable）。
            //    绝不能让它变成"TryMakePreToilReservations 返回 false" —— 那会走
            //    Pawn_JobTracker.cs:377-381，原版判 Errored 并挂一个 Wait（用户实测的几秒等待）。
            yield return GuardDest();

            // 1) 从容器取料到手上
            yield return WithdrawToHand();

            // 2) 走到目的地
            if (job.GetTarget(TargetIndex.A).IsValid)
            {
                yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch);
            }

            // 3) 地形蓝图：先拆掉这一格的旧地板（原版交付层那一步，见 RemoveExistingFloor 的注释）
            yield return RemoveExistingFloor();

            // 3.5) 工地被人占着就先等一会（见 WaitSiteClear —— "取出来又塞回去"的主循环源头）
            yield return WaitSiteClear();

            // 4) 蓝图 → Frame（原版 HaulToContainer 同款，解决 #5）
            yield return Toils_Construct.MakeSolidThingFromBlueprintIfNecessary(TargetIndex.A, TargetIndex.None);

            // 5) 塞容器优先（解决 #1 搬运死循环），失败则落地 + 移除 haul 标记
            yield return PlaceHauled();
        }

        // ---------- Toil 积木 ----------

        /// <summary>
        /// 目的地（蓝图 / Frame）被别人订走了就**干净地**判 <c>Incompletable</c>。
        ///
        /// <para>为什么不让它走"预订失败"那条路：见 <see cref="TryMakePreToilReservations"/> 的注释 ——
        /// 原版对"非排队 job 开起来之后预订失败"的处理是 <c>EndCurrentJob(Errored)</c> + 挂一个
        /// <c>Wait</c>，也就是用户实测的「几秒等待 job」。走 toil 判 <c>Incompletable</c> 才是干净的。</para>
        ///
        /// <para>右键"取到指定格"那条路的 <c>targetA</c> 是**格子**而不是 Thing ⇒ 直接放行。</para>
        /// </summary>
        private Toil GuardDest()
        {
            Toil toil = ToilMaker.MakeToil("DS_GuardDest");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = () =>
            {
                LocalTargetInfo dest = job.GetTarget(TargetIndex.A);
                if (!dest.IsValid) { EndJobWith(JobCondition.Incompletable); return; }

                Thing destThing = dest.Thing;
                if (destThing == null) return;              // 格子目的地：无需预订
                if (destThing.Destroyed) { EndJobWith(JobCondition.Incompletable); return; }

                if (pawn.HasReserved(dest, job)) return;     // 已经是我的
                if (pawn.Reserve(dest, job, 1, -1, null, false)) return; // 再试一次（errorOnFailed:false）
                EndJobWith(JobCondition.Incompletable);      // 别人订着 ⇒ 让工作分配重新挑一个
            };
            return toil;
        }

        /// <summary>
        /// 地形蓝图（地板 / 地基）那一格的「先拆旧地板」。
        ///
        /// <para><b>原版在哪做的</b>：<c>WorkGiver_ConstructDeliverResourcesToBlueprints.JobOnThing</c>（<c>:89-93</c>）
        /// 对地形蓝图**先**返回一个 <c>JobDefOf.RemoveFloor</c>（<c>ignoreDesignations:true</c>），
        /// 由 <c>JobDriver_RemoveFloor.DoEffect</c> → <c>TerrainGrid.RemoveTopLayer</c> 拆掉旧地板（材料掉落），
        /// 下一趟才送料 → 转框 → 完成时 <c>SetFoundation</c> 才成立。
        /// 殖民者走我们的取料作业（本 WorkGiver / <c>Patch_ResourceDeliverJobFor</c> 派发）绕过了那一步
        /// ⇒ 这里补上；否则完成时 <c>SetFoundation</c> 会因为「there is an under terrain there」被拒，
        /// 而那时框已经 <c>Destroy</c>（料与工全丢）。</para>
        ///
        /// <para>纯 Instant：本作业本来就是隔空取料，不该为这一步再排一次 200 工时的拆地板作业。
        /// 清上层会让原版 <c>DoTerrainChangedEffects</c> 重算这一格，可能**取消**这一格上建不下的蓝图 / 框
        /// ⇒ 目标没了就干净退出，绝不把手里的料塞进一个已经 Destroy 的容器。</para>
        /// </summary>
        private Toil RemoveExistingFloor()
        {
            var toil = ToilMaker.MakeToil("DS_RemoveExistingFloor");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = () =>
            {
                try
                {
                    Thing dest = job.GetTarget(TargetIndex.A).Thing;
                    var constructible = dest as IConstructible;
                    if (constructible == null) return;

                    if (!DSConstructionDelivery.RemoveExistingFloorFirstIfNeeded(constructible, pawn.Map)) return;
                    if (dest.Destroyed || !dest.Spawned) EndJobWith(JobCondition.Incompletable);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[DigitalStorage] 地形蓝图旧地板清理失败（本格退回原版行为）：" + e, 0x5D52F);
                }
            };
            return toil;
        }

        /// <summary>
        /// 把那件东西从容器取到手上。失败/背不下时**退回容器**，绝不让物品消失。
        /// </summary>
        private Toil WithdrawToHand()
        {
            var toil = ToilMaker.MakeToil("DS_WithdrawToHand");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = () =>
            {
                Pawn actor = toil.actor;
                Thing source = SourceThing;
                if (source == null || source.Destroyed) { EndJobWith(JobCondition.Incompletable); return; }

                IThingHolder holder = source.ParentHolder as IThingHolder;
                ThingOwner owner = holder?.GetDirectlyHeldThings();
                if (owner == null || !owner.Contains(source)) { EndJobWith(JobCondition.Incompletable); return; }
                Building_StorageCore core = holder as Building_StorageCore;
                if (core != null && !core.IsUsableNow) { EndJobWith(JobCondition.Incompletable); return; }

                // 裁剪到 pawn 负重上限（Bug #2）
                int maxCarry = actor.carryTracker.AvailableStackSpace(source.def);
                if (maxCarry <= 0) { EndJobWith(JobCondition.Incompletable); return; }
                int take = Math.Min(Math.Min(job.count, maxCarry), source.stackCount);
                if (take <= 0) { EndJobWith(JobCondition.Incompletable); return; }

                Thing taken;
                if (take >= source.stackCount)
                {
                    owner.Remove(source);
                    taken = source;
                }
                else
                {
                    taken = source.SplitOff(take);
                }
                if (taken == null) { EndJobWith(JobCondition.Incompletable); return; }

                // 携带拆堆会改变 taken.stackCount, 必须提前保存请求量
                int requested = taken.stackCount;
                int carried = actor.carryTracker.TryStartCarry(taken, requested, false);
                if (carried < requested)
                {
                    if (!taken.Destroyed && taken.holdingOwner == null && !owner.TryAdd(taken, true)
                        && GenPlace.TryPlaceThing(taken, actor.Position, actor.Map, ThingPlaceMode.Near, out Thing droppedPart))
                    {
                        // 落地即给"刚被取出"戳：否则下一轮 15 tick 扫描就把它吸回核心（取—塞循环）。
                        CompAutoIngest.MarkWithdrawn(droppedPart);
                    }
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                job.SetTarget(TargetIndex.B, actor.carryTracker.CarriedThing);
            };
            return toil;
        }

        /// <summary>
        /// 工地被别的 pawn 占着时**先等一会**，别让原版把整个作业判死。
        ///
        /// <para><b>为什么这是"取出来又塞回去"的主源头</b>：<c>Blueprint.TryReplaceWithSolidThing</c>
        /// （<c>RimWorld\Blueprint.cs:49-55</c>）一发现 <c>GenConstruct.FirstBlockingThing != null</c>
        /// 就 <c>workerPawn.jobs.EndCurrentJob(JobCondition.Incompletable)</c>，而阻挡物**包括站在占格里的
        /// 任何 pawn**（<c>GenConstruct.cs:900-903</c>）。作业一死，手里的料被
        /// <c>Pawn_JobTracker.CleanupCurrentJob</c> 丢在脚下 → 15 tick 内被自动收纳吸回核心 →
        /// WorkGiver 看到"核心有料、蓝图缺料"又派同一趟。工地上人来人往时，这就是一个稳定的死循环。</para>
        ///
        /// <para>只对<b>pawn</b>挡路才等（等它走开），其它阻挡物（墙、掉落物）等也没用 ⇒ 立刻交回原版判死。
        /// 最多等 4 游戏秒，仍挡着才 Incompletable（料有 <c>MarkWithdrawn</c> 窗口，不会立刻被吸回）。</para>
        /// </summary>
        private Toil WaitSiteClear()
        {
            int waited = 0;
            var toil = ToilMaker.MakeToil("DS_WaitSiteClear");
            toil.defaultCompleteMode = ToilCompleteMode.Never;
            toil.initAction = () => waited = 0;
            toil.tickAction = () =>
            {
                waited++;
                Thing dest = job.GetTarget(TargetIndex.A).Thing;
                if (dest == null || dest.Destroyed) { EndJobWith(JobCondition.Incompletable); return; }
                if (!(dest is Blueprint))
                {
                    ReadyForNextToil();   // 已经是 Frame / 不是蓝图：直接进下一段
                    return;
                }
                Thing blocker = GenConstruct.FirstBlockingThing(dest, pawn);
                if (blocker == null || !(blocker is Pawn))
                {
                    ReadyForNextToil();   // 没人挡 / 挡的是死物：交回原版按原样判死
                    return;
                }
                if (waited >= 240)
                {
                    EndJobWith(JobCondition.Incompletable);
                }
            };
            return toil;
        }

        /// <summary>
        /// 作业结束 / 被打断时，手里还没交付的料会被 <c>Pawn_JobTracker</c> 丢在脚下
        /// （<c>Pawn_JobTracker.cs:510-517</c>，不置禁止、也不打"刚被取出"戳）。
        /// 这里在丢之前先把戳盖上：落地时新补丁（<c>Patch_Thing_SpawnSetup_RearmWithdrawn</c>）
        /// 会再续一次窗口，保证"取料→路远→落地"这条路上不会被自动收纳当场吸回。
        /// <para>⚠️ <c>JobDriver.Cleanup</c> 在 1.6 里**不是 virtual**（`Verse.AI\JobDriver.cs:274`），
        /// 只能用 <c>AddFinishAction</c>（它在 <c>Cleanup</c> 内部第 276-280 行被调用，
        /// 早于 <c>Pawn_JobTracker.cs:516</c> 的落地）。</para>
        /// </summary>
        private void OnFinish()
        {
            try
            {
                Pawn p = pawn;
                if (p != null && p.carryTracker != null && p.carryTracker.CarriedThing != null)
                {
                    CompAutoIngest.MarkWithdrawn(p.carryTracker.CarriedThing);
                }
            }
            catch (Exception)
            {
                // 清理路径绝不抛异常
            }
            ReleaseEnroute();
        }

        private Toil PlaceHauled()
        {
            var toil = ToilMaker.MakeToil("DS_PlaceHauled");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = () =>
            {
                Pawn actor = toil.actor;
                Thing carried = actor.carryTracker.CarriedThing;
                if (carried == null) return;

                Thing targetThing = job.GetTarget(TargetIndex.A).Thing;

                // 构造场景：塞容器优先（不落地 → listerHaulables 看不到，#1）
                if (targetThing is IConstructible constructible)
                {
                    ThingOwner container = targetThing.TryGetInnerInteractableThingOwner();
                    if (container != null)
                    {
                        // ★ 按"还差多少"裁剪投料量。Frame 的内胆是 maxStacks=999999 的 ThingOwner
                        //   （`Frame.cs:170`），什么都能收 ⇒ 超发的料不会落地，而是在
                        //   `Frame.CompleteConstruction` 里被 `ClearAndDestroyContents()` **直接销毁**
                        //   （`Frame.cs:277`）。多出来的留在手上，作业结束时由 Cleanup 落地并带"刚被取出"戳。
                        int give = carried.stackCount;
                        try
                        {
                            int need = constructible.ThingCountNeeded(carried.def);
                            if (need >= 0 && need < give) give = need;
                        }
                        catch (Exception)
                        {
                            // 某些第三方 IConstructible 的 ThingCountNeeded 会抛（原版对安装蓝图也会）
                            // ⇒ 算不出来就不裁，维持原行为。
                        }
                        if (give > 0 && container.CanAcceptAnyOf(carried, true))
                        {
                            Thing taken = actor.carryTracker.innerContainer.Take(carried, give);
                            if (taken != null && container.TryAdd(taken, true))
                            {
                                // B2: 不设 forbidden。蓝图 reservation 已保护材料；建造失败时原版退回材料不会带 forbidden。
                                return;
                            }
                            if (taken != null)
                                actor.carryTracker.innerContainer.TryAdd(taken, true);
                        }
                    }
                }

                // 非构造场景（右键取料 / 容器放不进）：落地
                IntVec3 dropCell = actor.Position;
                LocalTargetInfo targetA = job.GetTarget(TargetIndex.A);
                if (targetA.HasThing)
                {
                    Thing t = targetA.Thing;
                    dropCell = t.Position;
                    if (t is IBillGiver giver)
                    {
                        foreach (IntVec3 c in giver.IngredientStackCells)
                        {
                            if (c.InBounds(actor.Map)
                                && GenPlace.HaulPlaceBlockerIn(carried, c, actor.Map, false) == null)
                            { dropCell = c; break; }
                        }
                    }
                }
                else if (targetA.IsValid)
                {
                    dropCell = targetA.Cell;
                }
                if (actor.carryTracker.TryDropCarriedThing(dropCell, ThingPlaceMode.Near, out Thing dropped, null))
                {
                    // ★ 落地就进自动收纳的"别碰"窗口（300 tick）：本作业的料是**给目的地用的**，
                    //   不是散落物。置禁止只挡得住走待搬表的那条路（ListerHaulables.ShouldBeHaulable
                    //   会把禁止物踢出去），而构造场景**刻意不置禁止**（原版建造失败退回材料时也不带
                    //   forbidden）⇒ 那条路上只能靠这个窗口挡住"刚放下就被吸回核心、小人再取一趟"的循环。
                    if (dropped != null)
                    {
                        // ★ 4.1 改：**不再置禁止**。
                        //   置禁止（`SetForbidden(true)`）原本是为了让原版待搬表看不到它
                        //   （`ListerHaulables.ShouldBeHaulable` 会把禁止物踢出去）。
                        //   但自动收纳现在**尊重禁止**（`CompAutoIngest.Reject.Forbidden`），
                        //   两者叠加的结果是"这块料永远躺在地上"。
                        //   现在统一靠"刚被取出"窗口顶住：`MarkWithdrawn` 起算 300 tick，
                        //   落地时再重算一次（`CompAutoIngest.TryRearmAfterSpawn`），
                        //   窗口过后它照常被自动收纳回核心 —— 材料不会凭空消失也不会滞留。
                        CompAutoIngest.MarkWithdrawn(dropped);
                    }
                }
            };
            return toil;
        }
    }

    /// <summary>
    /// 构造材料规划器 —— 从**容器内容物**里挑一种还没凑齐的材料。
    ///
    /// <para>4.0 与 3.0 的差别只在数据源：账本 <c>(def,stuff)+数量</c> → 真实的 Thing。
    /// 数量语义与原版 <c>WorkGiver_ConstructDeliverResources.ResourceDeliverJobFor</c> 对齐
    /// （forced → <c>ThingCountNeeded</c>；否则 <c>IHaulEnroute</c> 扣掉已送达 + enroute）。</para>
    /// </summary>
    public static class ConstructMaterialPlanner
    {
        /// <summary>
        /// 这个 pawn 能不能为本图的这件东西订下 <paramref name="count"/> 个。
        ///
        /// <para>跨图的源直接放行：原版 <c>ReservationManager.CanReserve</c> 有地图门
        /// （<c>target.Thing.MapHeld != map</c>），跨图**永远订不到**；
        /// 那边由取料 toil 的 <c>owner.Contains</c> 兜底（见 <see cref="JobDriver_DS_Withdraw"/>）。</para>
        /// </summary>
        public static bool CanReserveFor(Thing t, Pawn pawn, int count)
        {
            if (t == null || pawn == null) return false;
            if (t.MapHeld != pawn.Map) return true;
            return pawn.CanReserve(t, 1, count > 0 ? count : -1);
        }

        /// <summary>返回「要取的那件东西 + 取多少」。凑不出材料返回 null。</summary>
        public static (Thing thing, int count)? TryPlan(IConstructible c, Pawn pawn, bool forced, Map map)
        {
            // 安装蓝图（搬移已建成建筑/家具）没有材料账单，原版 TotalMaterialCost()
            // 会主动 Log.Error。必须跳过，交给原版安装流程处理。
            if (c is Blueprint_Install) return null;
            if (map == null) return null;

            List<ThingDefCountClass> materials;
            try
            {
                materials = c.TotalMaterialCost();
            }
            catch (Exception ex)
            {
                // 第三方 IConstructible 的 TotalMaterialCost 可能抛异常；
                // 原版路径由原版兜底，这里只跳过，避免整个 WorkGiver 扫描崩掉。
                Log.WarningOnce("[DigitalStorage] TotalMaterialCost failed for " + c.GetType().Name
                    + ": " + ex.Message, c.GetType().Name.GetHashCode());
                return null;
            }
            if (materials == null || materials.Count == 0) return null;

            for (int mi = 0; mi < materials.Count; mi++)
            {
                ThingDefCountClass need = materials[mi];
                if (need == null || need.thingDef == null || need.count <= 0) continue;

                int remaining;
                if (forced)
                {
                    remaining = c.ThingCountNeeded(need.thingDef);
                }
                else
                {
                    IHaulEnroute enroute = c as IHaulEnroute;
                    remaining = enroute != null
                        ? enroute.GetSpaceRemainingWithEnroute(need.thingDef, pawn)
                        : c.ThingCountNeeded(need.thingDef);
                }
                if (remaining <= 0) continue;

                ThingDef def = need.thingDef;
                // ★ 只挑"我这个 pawn 还订得到"的那一堆，而且按**本次要取的数量**订。
                //
                // 【为什么必须筛预订】（2026-10-02 用户实测）：
                //   原来按"最大堆"挑 ⇒ **所有 pawn 都收敛到同一堆**（日志里每个 job 的
                //   `B = Thing_UniversalMaterial274962` 是同一件东西）；只有第一个能订到，
                //   其余在 StartJob 时 `TryMakePreToilReservations` 返回 false ⇒ 原版
                //   Pawn_JobTracker.cs:377-381 一句 Log.Warning + EndCurrentJob(Errored)
                //   ⇒ 挂一个 Wait 恢复作业 ＝ 用户报的「完成建造后进入几秒等待 job」。
                //   1555 个蓝图的工地 + 一件材料 ⇒ 这不是偶发，是常态。
                // 按数量订而不是整堆：`ReservationManager.CanReserve` 会把同目标上别人已订的
                //   `stackCount` 累加后与堆总量比较 ⇒ **多个 pawn 可以各订同一大堆的一部分**，
                //   工地吞吐不会退化成"一次只能一个 pawn 干活"。
                Thing best = HaulSourceContents.FindBestIncludingRemote(map, t => t.stackCount,
                    t => t.def == def && CanReserveFor(t, pawn, Math.Min(remaining, t.stackCount)));
                if (best == null) continue;

                return (best, Math.Min(remaining, best.stackCount));
            }
            return null;
        }
    }
}
