using System.Collections.Generic;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>给囚犯送血原质</b>（典狱长）：把核心里的血原质包送到牢房。
    ///
    /// <para><b>挂点只有一个</b>：<c>RimWorld/WorkGiver_Warden_DeliverHemogen</c> 只重写了
    /// <c>JobOnThing</c>（<c>:9</c>），基类 <c>WorkGiver_Scanner.cs:35-37</c> 让
    /// <c>HasJobOnThing</c> 直接转调 <c>JobOnThing(pawn, t, forced) != null</c>
    /// ⇒ 这个方法在**每次扫描的每个候选囚犯**上都会跑一次（选定后再跑一次）。
    /// 所以这里绝不能放有副作用的操作，除非确定要出 job。</para>
    ///
    /// <para><b>为什么原版用不了</b>：<c>:44</c> 的
    /// <c>GenClosest.ClosestThingReachable(...)</c> 不传 <c>lookInHaulSources</c>，
    /// 且下游 <c>JobDriver_FoodDeliver.cs:62</c> 的 GotoThing 没有 <c>canGotoSpawnedParent</c>
    /// ⇒ 必须由我们把包取出到典狱长脚下。</para>
    ///
    /// <para><b>保守性</b>：重跑 :11-:43 的全部闸门（含两个私有辅助方法的等价实现），
    /// 全部通过才动核心 —— 囚犯已经够 / 牢房地上已有包 / 囚犯不在牢房等情况原版自己会拒。</para>
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_Warden_DeliverHemogen), nameof(WorkGiver_Warden_DeliverHemogen.JobOnThing))]
    static class Patch_WardenDeliverHemogenFromCore
    {
        static void Postfix(Pawn pawn, Thing t, bool forced, ref Job __result)
        {
            if (__result != null) return;
            if (pawn?.MapHeld == null) return;
            if (!ConsumePatchUtil.PawnMayUseCore(pawn)) return;
            if (!ModsConfig.BiotechActive) return;                       // :11
            if (!(t is Pawn prisoner)) return;
            if (!ShouldTakeCareOfPrisoner(pawn, prisoner, forced)) return; // :15
            if (!prisoner.guest.CanBeBroughtFood
                || !prisoner.Position.IsInPrisonCell(prisoner.Map)) return; // :20
            if (WardenFeedUtility.ShouldBeFed(prisoner)) return;           // :24 饿了交给原版送饭
            if (!(prisoner.genes?.GetGene(GeneDefOf.Hemogenic) is Gene_Hemogen gene)) return; // :28
            if (!gene.hemogenPacksAllowed) return;                         // :32
            if (!gene.ShouldConsumeHemogenNow()) return;                   // :36
            if (PackAlreadyAvailableFor(prisoner)) return;                 // :40

            Room prisonerRoom = prisoner.GetRoom();
            Thing taken = TakeUsablePack(pawn, prisoner, prisonerRoom);
            if (taken == null) return;

            // 照原版 :49-:51：DeliverFood + count=1 + 让囚犯站到能接的位置。
            Job job = JobMaker.MakeJob(JobDefOf.DeliverFood, taken, prisoner);
            job.count = 1;
            job.targetC = RCellFinder.SpotToChewStandingNear(prisoner, taken);
            __result = job;
        }

        /// <summary>
        /// 调 <see cref="WorkGiver_Scanner.HasJobOnThing"/> 的探测与真正建 job 会**各调一次**
        /// <c>JobOnThing</c>：基类 <c>WorkGiver_Scanner.cs:35-37</c> 把 HasJobOnThing 实现成
        /// <c>JobOnThing(...) != null</c>，而探测时返回的那个 job 会被丢弃。
        /// 如果每次都抽，地上会白留一个包 —— 所以用「同一典狱长 + 同一囚犯 + 刚抽出」做 2 tick 复用窗口。
        /// </summary>
        private static Thing TakeUsablePack(Pawn warden, Pawn prisoner, Room prisonerRoom)
        {
            int now = Find.TickManager.TicksGame;

            Prepared prepared;
            if (preparedByWarden.TryGetValue(warden, out prepared))
            {
                preparedByWarden.Remove(warden);
                if (prepared.Pack != null && !prepared.Pack.Destroyed
                    && prepared.Pack.Spawned && prepared.Pack.Map == warden.MapHeld
                    && prepared.Prisoner == prisoner && now - prepared.Tick <= 2)
                {
                    return prepared.Pack;
                }
            }

            Thing pack = HaulSourceContents.FindBestIncludingRemote(
                warden.MapHeld, null,
                x => x.def == ThingDefOf.HemogenPack
                     && !x.IsForbidden(warden)
                     && warden.CanReserve(x)
                     && RoomOfStorage(x) != prisonerRoom);
            if (pack == null) return null;

            Thing taken = HaulSourceContents.ExtractToFeet(pack, 1, warden);
            if (taken == null) return null;

            preparedByWarden[warden] = new Prepared { Pack = taken, Prisoner = prisoner, Tick = now };
            return taken;
        }

        private struct Prepared
        {
            public Thing Pack;
            public Pawn Prisoner;
            public int Tick;
        }

        private static readonly Dictionary<Pawn, Prepared> preparedByWarden = new Dictionary<Pawn, Prepared>();

        /// <summary>
        /// <c>WorkGiver_Warden.cs:24-31</c> 的等价实现（原方法是 protected，无法直接调）。
        /// 任何一条不满足都不许翻案 —— 那是"这个囚犯现在不该被照顾"，与有没有血包无关。
        /// </summary>
        private static bool ShouldTakeCareOfPrisoner(Pawn warden, Pawn prisoner, bool forced)
        {
            if (!prisoner.IsPrisonerOfColony
                || !prisoner.guest.PrisonerIsSecure
                || !prisoner.Spawned
                || prisoner.InAggroMentalState
                || prisoner.IsForbidden(warden)
                || prisoner.IsFormingCaravan())
            {
                return false;
            }
            return warden.CanReserveAndReach(prisoner, PathEndMode.OnCell, warden.NormalMaxDanger(), 1, -1, null, forced);
        }

        /// <summary>
        /// <c>WorkGiver_Warden_DeliverHemogen.cs:55-78</c> 的等价实现（原方法 private）。
        /// 囚犯身上 / 背包里 / 牢房里已经有包 ⇒ 原版会拒，我们也不插手。
        /// </summary>
        private static bool PackAlreadyAvailableFor(Pawn prisoner)
        {
            if (prisoner.carryTracker.CarriedCount(ThingDefOf.HemogenPack) > 0) return true;
            if (prisoner.inventory.Count(ThingDefOf.HemogenPack) > 0) return true;

            Room room = prisoner.GetRoom();
            if (room != null)
            {
                List<Region> regions = room.Regions;
                for (int i = 0; i < regions.Count; i++)
                {
                    if (regions[i].ListerThings.ThingsOfDef(ThingDefOf.HemogenPack).Count > 0) return true;
                }
            }
            return false;
        }

        /// <summary>容器内容物本身未 Spawned，房间要看它的宿主建筑（与 :44 的 <c>pack.GetRoom() != 囚犯房</c> 同义）。</summary>
        private static Room RoomOfStorage(Thing stored)
        {
            return (stored?.ParentHolder as Thing)?.GetRoom();
        }
    }
}
