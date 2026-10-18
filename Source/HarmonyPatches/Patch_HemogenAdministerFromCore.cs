using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>施用血原质</b>：医生把核心里的血原质包取出来喂给缺血的人。
    ///
    /// <para><b>为什么原版用不了</b>：<c>RimWorld/Workgiver_AdministerHemogen</c> 有两处
    /// 「找血原质包」，<c>:57</c>（HasJobOnThing，判"该不该去"）与 <c>:68</c>（JobOnThing，建 job），
    /// 都是 <c>GenClosest.ClosestThingReachable(...)</c> <b>不传 <c>lookInHaulSources</c></b>
    /// ⇒ 只看已生成的包（<c>Verse/GenClosest.cs:40</c> 默认 false）。
    /// 而下游 <c>JobDriver_FoodFeedPatient.cs:66</c> 的 GotoThing <b>没有</b>
    /// <c>canGotoSpawnedParent</c> ⇒ 就算把容器内容物塞进 job 也走不到。</para>
    ///
    /// <para><b>所以两手都要做</b>：判定层（把 false 翻成 true）+ 建 job 层（把包取出到医生脚下，
    /// 再交给原版 <c>JobDefOf.FeedPatient</c>，job 语义一字不改）。</para>
    ///
    /// <para><b>保守性</b>：只有「把原版 :32-:53 的闸门全部重跑一遍仍然通过」并且
    /// 「地图上确实没有可达的已生成包」才翻案 —— 也就是说，拒绝原因只能是"没有已生成的血包"。
    /// 病人血原质已满 / 不需要喂 / 已被人预约等情况原版会自己拒，这里绝不推翻。</para>
    /// </summary>
    static class Patch_HemogenAdministerFromCore
    {
        /// <summary>
        /// 重跑 <c>Workgiver_AdministerHemogen.HasJobOnThing</c> 里**血包搜索之前**的所有闸门
        /// （<c>:23-:56</c>）。任何一条不通过就说明原版的拒绝原因不是"没有血包"，不许翻案。
        /// </summary>
        internal static bool GatesBeforePackSearchPass(Pawn doctor, Thing target, bool forced)
        {
            if (!ConsumePatchUtil.PawnMayUseCore(doctor)) return false;
            if (!(target is Pawn patient) || patient == doctor) return false;

            Gene_Hemogen gene = patient.genes?.GetFirstGeneOfType<Gene_Hemogen>();
            if (gene == null) return false;
            if (gene.ValuePercent >= 0.95f) return false;              // :32 血原质已满
            if (!forced && gene.Value >= 0.25f) return false;          // :36 非强制时不到 0.25 不去
            if (!FeedPatientUtility.ShouldBeFed(patient)) return false; // :40
            if (!gene.hemogenPacksAllowed) return false;               // :44
            if (!gene.ShouldConsumeHemogenNow()) return false;         // :48
            if (!doctor.CanReserve(target, 1, -1, null, forced)) return false; // :53
            return true;
        }

        /// <summary>
        /// 原版那次搜索（<c>:57</c> / <c>:68</c>）的同一个查询：有没有**已生成**且可达可约的包。
        /// 与判定层完全同参，避免"我以为没有、其实有"的误翻案。
        /// </summary>
        internal static bool SpawnedPackReachable(Pawn doctor)
        {
            return GenClosest.ClosestThingReachable(
                doctor.Position, doctor.Map,
                ThingRequest.ForDef(ThingDefOf.HemogenPack),
                PathEndMode.OnCell, TraverseParms.For(doctor), 9999f,
                pack => !pack.IsForbidden(doctor) && doctor.CanReserve(pack)) != null;
        }

        /// <summary>容器里找一包可用的血原质（本图优先，本图没有再看其它图核心）。</summary>
        internal static Thing FindPackInStorage(Pawn doctor)
        {
            if (doctor.MapHeld == null) return null;
            return HaulSourceContents.FindBestIncludingRemote(
                doctor.MapHeld, null,
                t => t.def == ThingDefOf.HemogenPack
                     && !t.IsForbidden(doctor)
                     && doctor.CanReserve(t));
        }
    }

    /// <summary>判定层：把「核心里有包」这一种情形补上（原版 <c>:57</c> 的缺口）。</summary>
    [HarmonyPatch(typeof(Workgiver_AdministerHemogen), nameof(Workgiver_AdministerHemogen.HasJobOnThing))]
    static class Patch_HemogenAdminister_HasJobOnThing
    {
        static void Postfix(Pawn pawn, Thing t, bool forced, ref bool __result)
        {
            if (__result) return;
            if (pawn?.MapHeld == null) return;
            if (!Patch_HemogenAdministerFromCore.GatesBeforePackSearchPass(pawn, t, forced)) return;
            if (Patch_HemogenAdministerFromCore.SpawnedPackReachable(pawn)) return;
            if (HaulSourceContents.CountOf(pawn.MapHeld, ThingDefOf.HemogenPack) <= 0) return;

            __result = true;
        }
    }

    /// <summary>建 job 层：取出包放到医生脚下，再照原版 :71-:73 造 <c>FeedPatient</c>。</summary>
    [HarmonyPatch(typeof(Workgiver_AdministerHemogen), nameof(Workgiver_AdministerHemogen.JobOnThing))]
    static class Patch_HemogenAdminister_JobOnThing
    {
        static void Postfix(Pawn pawn, Thing t, bool forced, ref Job __result)
        {
            if (__result != null) return;
            if (pawn?.MapHeld == null) return;
            if (!Patch_HemogenAdministerFromCore.GatesBeforePackSearchPass(pawn, t, forced)) return;

            // 已生成的包能走到这里（原版非 null）说明前面的 Postfix 不该插手；这里只处理容器。
            Thing pack = Patch_HemogenAdministerFromCore.FindPackInStorage(pawn);
            if (pack == null) return;

            Thing taken = HaulSourceContents.ExtractToFeet(pack, 1, pawn);
            if (taken == null) return;

            Job job = JobMaker.MakeJob(JobDefOf.FeedPatient, taken, t);
            job.count = 1;
            __result = job;
        }
    }
}
