using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>读档兜底：job 的目标在存档里"整段丢了"就不要重建 toils。</b>
    ///
    /// <para><b>现象</b>（2026-10-23 实机，用户"存档然后立马加载该存档"）：
    /// <c>Exception in SetupToils for pawn Alaina driver=JobDriver_ConstructFinishFrame …</c>
    /// <c>NullReferenceException at JobDriver_ConstructFinishFrame.get_IsBuildingAttachment</c>，
    /// 紧接着错误恢复的 <c>JobDriver_Wait</c> 在 initAction 里再炸一次，最后
    /// "…the pawn is now jobless…"。</para>
    ///
    /// <para><b>机制</b>：保存时若 <c>job.targetA</c> 的 Thing 已被销毁，
    /// <c>Verse\Scribe_TargetInfo.cs</c>（saving 分支）会**整段不写**这个元素 ——
    /// 不报错、日志里也查不到。读档时它静默变成 <c>LocalTargetInfo.Invalid</c>，
    /// 于是 <c>MakeNewToils</c> 第一行 <c>Frame.def</c> 就 NRE
    /// （<c>Frame</c> = <c>job.targetA.Thing</c> = null）；而原版的 catch 只能起
    /// recovery job，那个 Wait job 在 <c>base.Map.pawnDestinationReservationManager</c>
    /// 处又炸，pawn 直接变 jobless。</para>
    ///
    /// <para><b>触发来源</b>：本 mod 的代理会抢在殖民者前面把东西删掉（建完框、拆完建筑、
    /// 蓝图换框），删的那一刻殖民者手上的 job 就指着这件已销毁的东西 ⇒ 只要这一瞬间落进存档，
    /// 下次读档必炸。来源侧已在 <see cref="DigitalStorage.AI.ForeignJobRelease"/> 收掉别人的 job；
    /// 本前缀是**二道防线**：不管坏 job 是谁写进存档的（第三方 mod、旧存档、原版竞态），
    /// 读档时先安全结束它，别让 vanilla 拿着 null 去解引用。</para>
    ///
    /// <para><b>只处理"目标全丢"这一种坏状态</b>：A/B/C 三个目标全部 Invalid 且 A/B 队列为空。
    /// 这是"本来是 Thing 目标、保存时被整段丢掉"的形状；格子目标（Goto / LayDown / Wait 等）
    /// 的 <c>Cell</c> 是有效的，不会被误伤。此外再放过司机类是 Wait / Goto / LayDown /
    /// Meditate / WaitDowned 的 job —— 它们本来就允许没有目标（如冥想、站岗），
    /// 误结束会打断玩家摆好的姿态。</para>
    ///
    /// <para><b>安全阀</b>：只有在 <c>Scribe.mode == PostLoadInit</c> 且
    /// <c>pawn.jobs.curJob</c> 就是这条 job 时才动手；结束时用
    /// <c>EndCurrentJob(JobCondition.Incompletable, startNewJob: false)</c>（原版
    /// <c>Toil.FailOnDespawnedOrNull</c> 同款条件），异常整段吞掉并放行 vanilla 的旧路径。</para>
    /// </summary>
    [HarmonyPatch(typeof(JobDriver), "SetupToils")]
    internal static class Patch_JobDriver_SetupToils_LoadGuard
    {
        private static bool Prefix(JobDriver __instance)
        {
            if (Scribe.mode != LoadSaveMode.PostLoadInit) return true;

            try
            {
                Pawn pawn = __instance.pawn;
                Job job = __instance.job;
                if (pawn == null || job == null || pawn.jobs == null) return true;
                if (!ReferenceEquals(pawn.jobs.curJob, job)) return true;
                if (!AllTargetsLost(job)) return true;
                if (TargetlessByDesign(job)) return true;

                Log.Warning("[DigitalStorage] 读档时发现 job 的目标在存档里已丢失（保存时目标已销毁，Scribe 不写空引用），"
                    + "已安全结束该 job 以免原版 MakeNewToils 解引用 null："
                    + pawn.LabelShortCap + " / " + (job.def != null ? job.def.defName : "?"));
                pawn.jobs.EndCurrentJob(JobCondition.Incompletable, startNewJob: false);
                return false;   // 跳过 SetupToils：不要拿 null 目标去建 toils
            }
            catch (Exception e)
            {
                // 兜底失败就退回原版行为（让它照旧报错），至少不吞掉原有信息。
                Log.ErrorOnce("[DigitalStorage] 读档清理坏 job 失败（退回原版行为）：" + e, 0x5D532);
                return true;
            }
        }

        private static bool AllTargetsLost(Job job)
        {
            if (!Lost(job.targetA) || !Lost(job.targetB) || !Lost(job.targetC)) return false;
            if (job.targetQueueA != null && job.targetQueueA.Count > 0) return false;
            if (job.targetQueueB != null && job.targetQueueB.Count > 0) return false;
            return true;
        }

        private static bool Lost(LocalTargetInfo target)
        {
            return !target.IsValid;
        }

        /// <summary>"允许没有目标"的司机类：这些 job 本来就是站/走/躺/冥想，误杀会打断玩家摆的姿态。</summary>
        private static bool TargetlessByDesign(Job job)
        {
            Type driver = job.def != null ? job.def.driverClass : null;
            if (driver == null) return true;   // 没司机的 job 原版自己会清理
            return typeof(JobDriver_Wait).IsAssignableFrom(driver)
                || typeof(JobDriver_Goto).IsAssignableFrom(driver)
                || typeof(JobDriver_LayDown).IsAssignableFrom(driver)
                || typeof(JobDriver_Meditate).IsAssignableFrom(driver)
                || typeof(JobDriver_WaitDowned).IsAssignableFrom(driver);
        }
    }
}
