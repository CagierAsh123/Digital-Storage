using System;
using HarmonyLib;
using RimWorld;

namespace DigitalStorage.HarmonyPatches
{
    [HarmonyPatch(typeof(CompRottable), "Active", MethodType.Getter)]
    public static class Patch_CompRottable_Active
    {
        public static bool Prefix(CompRottable __instance, ref bool __result)
        {
            return true;
        }
    }
}

