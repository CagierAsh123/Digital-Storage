using System;
using HarmonyLib;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [HarmonyPatch(typeof(Thing), "TryAbsorbStack")]
    public static class Patch_Thing_TryAbsorbStack
    {
        public static bool Prefix(Thing __instance, Thing other, ref bool __result)
        {
            bool flag = Patch_StackProtection.IsProtectedDisk(__instance) || Patch_StackProtection.IsProtectedDisk(other);
            bool flag2;
            if (flag)
            {
                __result = false;
                flag2 = false;
            }
            else
            {
                flag2 = true;
            }
            return flag2;
        }
    }
}

