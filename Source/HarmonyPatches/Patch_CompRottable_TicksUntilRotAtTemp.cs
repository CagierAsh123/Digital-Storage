using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [HarmonyPatch(typeof(CompRottable), "TicksUntilRotAtTemp")]
    public static class Patch_CompRottable_TicksUntilRotAtTemp
    {
        public static bool Prefix(CompRottable __instance, float temp, ref int __result)
        {
            // 不改动全局腐烂速度。
            // 核心上的物品是否腐烂由 Patch_CompRottable_Active 控制（核心上直接禁用腐烂）。
            return true;
        }
    }
}

