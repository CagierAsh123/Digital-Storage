using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using DigitalStorage.HarmonyPatches;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// I1.5：从容器取消耗品 → carryTracker → 原版 ingest toils 处理进嘴。
    ///
    /// <para><b>4.0 改造</b>：要吃的那个东西**直接就是 <c>job.targetA</c>**
    /// （由 <see cref="ConsumptionHelper"/> 在创 job 时放进去的真实 Thing），
    /// 不再有 <c>ItemKey</c>、不再有挂在 job 上的"计划"字典 —— 那个字典不参与 Scribe，
    /// 存档读档会丢，是个隐患。现在 job 目标本身就会被序列化。</para>
    ///
    /// <para>「纯轮椅」：不再走代理点（<c>TargetIndex.B</c> 曾经是代理格），
    /// 直接从容器取出到手上，然后去找桌/椅吃。</para>
    /// </summary>
    public class JobDriver_DS_Consume : JobDriver
    {
        /// <summary>要消耗的那件真实东西（取出前在容器里，取出后在 carryTracker 上）。</summary>
        private Thing TargetThing => job.GetTarget(TargetIndex.A).Thing;

        /// <summary>它所在的容器（取出前有效）。</summary>
        private Building_StorageCore TargetCore => job.GetTarget(TargetIndex.C).Thing as Building_StorageCore;

        public override string GetReport()
        {
            Thing t = TargetThing;
            return "DS_ReportConsuming".Translate(t != null ? t.Label : "?");
        }

        /// <summary>
        /// <b>⚠️ 绝不能返回 false</b>：<c>Pawn_JobTracker.StartJob:377-381</c> 对"非排队 job 开起来
        /// 之后预订失败"只有一句 <c>Log.Warning</c> + <c>EndCurrentJob(JobCondition.Errored)</c>，
        /// 而 Errored 会让原版挂一个 <c>Wait</c> 恢复作业（用户 2026-10-02 实测的"几秒等待 job"）。
        ///
        /// <para>抢不到就直接放行：<c>MakeNewToils</c> 的 <c>FailOn</c> 与 <see cref="WithdrawToCarry"/>
        /// 的 <c>owner.Contains</c> 会把"东西没了/被别人拿走"判成 <c>Incompletable</c>，那是干净的路。</para>
        /// </summary>
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            Thing target = TargetThing;
            if (target == null || target.Destroyed) return true;   // 交给 FailOn / WithdrawToCarry 判

            // 【跨图】原版 ReservationManager.CanReserve 有一条硬闸门：
            //   target.Thing.SpawnedOrAnyParentSpawned && target.Thing.MapHeld != map ⇒ false
            // a 图核心里的东西 MapHeld 就是 a 图，b 图的预订管理器**永远订不到**它。
            // 跨图这条路本来也不需要它：取料 toil 会在同一次 initAction 里就把东西挪到手上。
            if (target.MapHeld != pawn.Map) return true;

            // 预订那件具体的东西（未 Spawned 也没问题 —— Thing.MapHeld 由容器的
            // ParentHolder => Map 数据修复保证非 null）。这样两个 pawn 不会抢同一份。
            // errorOnFailed:false —— 抢不到也不硬失败。
            pawn.Reserve(target, job, 1, -1, null, false);
            return true;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            // ★ 作业结束/被打断时，手上剩余的消耗品会被原版丢在脚下
            //   （`Verse.AI\Pawn_JobTracker.cs:510-517`，不置禁止、不打标记）⇒ 自动收纳
            //   15 tick 一轮就把它吸回核心 ⇒ 还是饿/该用药，再派一趟 ⇒ "取—吸"循环。
            //   丢之前先盖戳。`JobDriver.Cleanup` 在 1.6 里**不是 virtual**（`Verse.AI\JobDriver.cs:274`），
            //   只能挂 `AddFinishAction`（它在 Cleanup 内部第 276-280 行执行，早于第 516 行的落地）。
            AddFinishAction(_ => MarkCarriedForAutoIngest());

            // 容器被拆/断电，或那件东西没了 → 放弃（原版 JobGiver 会重试）
            this.FailOn(() =>
            {
                Building_StorageCore core = TargetCore;
                if (core != null && (!core.Spawned || !core.Powered)) return true;
                Thing t = TargetThing;
                return t == null || t.Destroyed;
            });

            // 放下手上异物 → 取料 → 走到 chair/spot → 找桌子 → 嚼 → 吞
            yield return DropIncompatibleCarryIfNeeded();
            yield return WithdrawToCarry();

            yield return CarryToChewSpot();
            yield return Toils_Ingest.FindAdjacentEatSurface(TargetIndex.B, TargetIndex.A);

            var chewing = Toils_Ingest.ChewIngestible(pawn, ChewDurationMultiplier, TargetIndex.A, TargetIndex.B)
                .FailOn((Toil x) => pawn.carryTracker.CarriedThing == null
                                    || !pawn.carryTracker.CarriedThing.IngestibleNow)
                .FailOnCannotTouch(TargetIndex.A, PathEndMode.Touch);
            yield return chewing;
            yield return Toils_Ingest.FinalizeIngest(pawn, TargetIndex.A);
        }

        /// <summary>盖上"刚被取出"戳（<see cref="CompAutoIngest.MarkWithdrawn(Thing)"/>）。</summary>
        private void MarkCarriedForAutoIngest()
        {
            try
            {
                Thing carried = pawn != null ? pawn.carryTracker.CarriedThing : null;
                if (carried != null && !carried.Destroyed) CompAutoIngest.MarkWithdrawn(carried);
            }
            catch (Exception)
            {
                // 清理路径绝不抛异常
            }
        }

        // ---------- toil builders ----------

        private Toil DropIncompatibleCarryIfNeeded()
        {
            var toil = ToilMaker.MakeToil("DS_DropIncompatible");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = () =>
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                Thing target = TargetThing;
                if (carried != null && target != null && carried.def != target.def)
                {
                    // 落地即盖"刚被取出"戳：否则自动收纳 15 tick 一轮就把它吸回核心。
                    if (pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out Thing dropped)
                        && dropped != null && !dropped.Destroyed)
                    {
                        CompAutoIngest.MarkWithdrawn(dropped);
                    }
                }
            };
            return toil;
        }

        /// <summary>
        /// 把要消耗的东西从容器里取到手上。
        ///
        /// 取出失败/背不下时**退回容器**，绝不让物品消失 —— 3.0 是"退回账本"，
        /// 4.0 是"退回 ThingOwner"。
        /// </summary>
        private Toil WithdrawToCarry()
        {
            var toil = ToilMaker.MakeToil("DS_WithdrawToCarry");
            toil.defaultCompleteMode = ToilCompleteMode.Instant;
            toil.initAction = () =>
            {
                Thing source = TargetThing;
                if (source == null || source.Destroyed) { Fail(); return; }

                IThingHolder holder = source.ParentHolder as IThingHolder;
                ThingOwner owner = holder?.GetDirectlyHeldThings();
                if (owner == null || !owner.Contains(source)) { Fail(); return; }

                int maxCarry = pawn.carryTracker.AvailableStackSpace(source.def);
                if (maxCarry <= 0) { Fail(); return; }

                int take = Math.Min(Math.Min(job.count, maxCarry), source.stackCount);
                if (take <= 0) { Fail(); return; }

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
                if (taken == null) { Fail(); return; }

                int carried = pawn.carryTracker.TryStartCarry(taken, taken.stackCount, false);
                if (carried < taken.stackCount)
                {
                    // 背不下（或部分背下）→ 剩余部分退回容器，避免消失
                    ReturnToOwner(owner, taken);
                    Fail();
                    return;
                }

                job.SetTarget(TargetIndex.A, pawn.carryTracker.CarriedThing);
            };
            return toil;
        }

        private void ReturnToOwner(ThingOwner owner, Thing t)
        {
            if (t == null || t.Destroyed) return;
            if (owner != null && owner.TryAdd(t, true)) return;
            // 极端情况（容器满了）：就地落地，总比消失好
            if (t.Spawned) return;
            // ★ 落在小人脚下而不是地图中心（中心可能很远、也可能不可达）；
            //   落地后立刻盖"刚被取出"戳，否则自动收纳 15 tick 一轮就把它吸回核心 ——
            //   玩家看到的正是"吃饭/吃药的余料在核心与脚下之间来回飞"。
            Map map = (pawn != null ? pawn.Map : null) ?? Find.CurrentMap;
            if (map == null) return;
            IntVec3 pos = (pawn != null && pawn.Spawned) ? pawn.Position : map.Center;
            if (!pos.IsValid) pos = map.Center;
            if (GenPlace.TryPlaceThing(t, pos, map, ThingPlaceMode.Near, out Thing dropped)
                && dropped != null && !dropped.Destroyed)
            {
                CompAutoIngest.MarkWithdrawn(dropped);
            }
        }

        private Toil CarryToChewSpot()
        {
            var toil = ToilMaker.MakeToil("DS_CarryToChewSpot");
            toil.initAction = () =>
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                if (carried == null) { Fail(); return; }

                IntVec3 spot;
                if (Toils_Ingest.TryFindChairOrSpot(pawn, carried, out spot))
                {
                    pawn.ReserveSittableOrSpot(spot, job, true);
                    pawn.Map.pawnDestinationReservationManager.Reserve(pawn, job, spot);
                    // TargetIndex.B 语义 = "吃的地方"（Toils_Ingest.FindAdjacentEatSurface 用它找桌子）。
                    // 旧实现把 B 当作代理格传进来，语义是错的；这里改成吃的位置本身。
                    job.SetTarget(TargetIndex.B, spot);
                    pawn.pather.StartPath(spot, PathEndMode.OnCell);
                    return;
                }
                // 无 chair/spot → 原地吃
                job.SetTarget(TargetIndex.B, pawn.Position);
                pawn.pather.StartPath(pawn.Position, PathEndMode.OnCell);
            };
            toil.defaultCompleteMode = ToilCompleteMode.PatherArrival;
            return toil;
        }

        private float ChewDurationMultiplier
        {
            get
            {
                Thing target = TargetThing;
                ThingDef def = target?.def;
                if (def?.ingestible != null && !def.ingestible.useEatingSpeedStat) return 1f;
                return 1f / pawn.GetStatValue(StatDefOf.EatingSpeed, true, -1);
            }
        }

        private void Fail()
        {
            ConsumePatchUtil.NotifyFail(pawn);
            EndJobWith(JobCondition.Incompletable);
        }
    }
}
