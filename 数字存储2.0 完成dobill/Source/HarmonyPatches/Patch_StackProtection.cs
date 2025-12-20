using System;
using System.Collections.Generic;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [StaticConstructorOnStartup]
    public static class Patch_StackProtection
    {
        static Patch_StackProtection()
        {
            LongEventHandler.QueueLongEvent(new Action(Patch_StackProtection.EnforceStackLimits), "DigitalStorage_EnforceStackLimits", false, null, true, false, null);
        }

        private static void EnforceStackLimits()
        {
            foreach (string defName in Patch_StackProtection.ProtectedDiskDefs)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                bool flag = def != null && def.stackLimit != 1;
                if (flag)
                {
                    Log.Message(string.Format("[数字存储] 强制设置 {0} 堆叠限制为 1 (原值: {1})", defName, def.stackLimit));
                    def.stackLimit = 1;
                }
            }
        }

        public static bool IsProtectedDisk(ThingDef def)
        {
            return def != null && Patch_StackProtection.ProtectedDiskDefs.Contains(def.defName);
        }

        public static bool IsProtectedDisk(Thing thing)
        {
            return ((thing != null) ? thing.def : null) != null && Patch_StackProtection.ProtectedDiskDefs.Contains(thing.def.defName);
        }

        private static readonly HashSet<string> ProtectedDiskDefs = new HashSet<string> { "DigitalStorage_DiskSmall", "DigitalStorage_DiskMedium", "DigitalStorage_DiskLarge" };
    }
}

