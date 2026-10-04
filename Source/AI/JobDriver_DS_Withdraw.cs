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

            return true;
        }

        public override void Notify_Starting()
        {
            base.Notify_Starting();

            // 右键路径：调用方已经把 Thing + count 放进 job 了
            if (job.count > 0 && SourceThing != null) return;

            // 构造场景：现场规划材料（蓝图 / Frame → 容器里凑得出的那种）
            Thing target = job.GetTarget(TargetIndex.A).Thing;
            IConstructible constructible = target as IConstructible;
            if (constructible == null)
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }

            var plan = ConstructMaterialPlanner.TryPlan(constructible, pawn, job.playerForced, pawn.Map);
            if (plan == null)
            {
                EndJobWith(JobCondition.Incompletable);
                return;
            }

            Thing src = plan.Value.thing;
            job.SetTarget(TargetIndex.B, src);
            job.SetTarget(TargetIndex.C, src.ParentHolder as Thing);
            job.count = plan.Value.count;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
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

            // 3) 蓝图 → Frame（原版 HaulToContainer 同款，解决 #5）
            yield return Toils_Construct.MakeSolidThingFromBlueprintIfNecessary(TargetIndex.A, TargetIndex.None);

            // 4) 塞容器优先（解决 #1 搬运死循环），失败则落地 + 移除 haul 标记
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
                    if (!taken.Destroyed && taken.holdingOwner == null && !owner.TryAdd(taken, true))
                        GenPlace.TryPlaceThing(taken, actor.Position, actor.Map, ThingPlaceMode.Near);
                    EndJobWith(JobCondition.Incompletable);
                    return;
                }

                job.SetTarget(TargetIndex.B, actor.carryTracker.CarriedThing);
            };
            return toil;
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
                if (targetThing is IConstructible)
                {
                    ThingOwner container = targetThing.TryGetInnerInteractableThingOwner();
                    if (container != null && container.CanAcceptAnyOf(carried, true))
                    {
                        Thing taken = actor.carryTracker.innerContainer.Take(carried, carried.stackCount);
                        if (taken != null && container.TryAdd(taken, true))
                        {
                            // B2: 不设 forbidden。蓝图 reservation 已保护材料；建造失败时原版退回材料不会带 forbidden。
                            return;
                        }
                        if (taken != null)
                            actor.carryTracker.innerContainer.TryAdd(taken, true);
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
                    if (dropped != null && !(targetThing is IConstructible))
                        dropped.SetForbidden(true, false);
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
