using System;
using HarmonyLib;
using RimWorld;

namespace DigitalStorage.HarmonyPatches
{
    [HarmonyPatch(typeof(CompRottable), "TicksUntilRotAtTemp")]
    public static class Patch_CompRottable_TicksUntilRotAtTemp
    {
        public static bool Prefix(CompRottable __instance, ref int __result)
        {
            return true;
        }
    }
}

