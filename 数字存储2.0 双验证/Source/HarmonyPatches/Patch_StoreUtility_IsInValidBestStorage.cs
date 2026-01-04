using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [HarmonyPatch(typeof(StoreUtility), "IsInValidBestStorage")]
    public static class Patch_StoreUtility_IsInValidBestStorage
    {
        public static void Postfix(Thing t, ref bool __result)
        {
            // 基类已设置 PassThroughOnly，核心建筑可通行，不需要特殊处理
        }
    }
}

