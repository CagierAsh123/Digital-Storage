using HarmonyLib;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 修复 PickUpAndHaul 的 Invalid count 错误
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), "StartJob")]
    public static class Patch_PickUpAndHaul_FixJobCount
    {
        public static void Prefix(Job newJob, Pawn ___pawn)
        {
            if (newJob == null || ___pawn == null)
            {
                return;
            }

            // 只处理 HaulToInventory 任务
            if (newJob.def.defName != "HaulToInventory")
            {
                return;
            }

            // 修复 targetQueueA 中的 count
            if (newJob.targetQueueA != null && newJob.countQueue != null)
            {
                for (int i = 0; i < newJob.countQueue.Count; i++)
                {
                    if (newJob.countQueue[i] <= 0)
                    {
                        // 如果 count 是负数或 0，尝试从 targetQueueA 获取正确的值
                        if (i < newJob.targetQueueA.Count && newJob.targetQueueA[i].HasThing)
                        {
                            Thing thing = newJob.targetQueueA[i].Thing;
                            newJob.countQueue[i] = thing.stackCount;
                        }
                        else
                        {
                            newJob.countQueue[i] = 1;
                        }
                    }
                }
            }

            // 修复主 count
            if (newJob.count <= 0 && newJob.targetA.HasThing)
            {
                newJob.count = newJob.targetA.Thing.stackCount;
            }
        }
    }

    /// <summary>
    /// 在错误检查时修复 count（双重保险）
    /// </summary>
    [HarmonyPatch(typeof(Toils_Haul), "ErrorCheckForCarry")]
    public static class Patch_PickUpAndHaul_ErrorCheckForCarry
    {
        public static void Prefix(Pawn pawn, Thing haulThing)
        {
            if (pawn == null || haulThing == null)
            {
                return;
            }

            Job curJob = pawn.CurJob;
            if (curJob == null)
            {
                return;
            }

            // 修复负数或无效的 count
            if (curJob.count <= 0 || curJob.count > haulThing.stackCount)
            {
                curJob.count = haulThing.stackCount;
            }
        }
    }
}

