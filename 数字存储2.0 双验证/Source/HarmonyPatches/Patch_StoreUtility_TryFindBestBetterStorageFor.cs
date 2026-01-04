using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [HarmonyPatch(typeof(StoreUtility), "TryFindBestBetterStorageFor")]
    public static class Patch_StoreUtility_TryFindBestBetterStorageFor
    {
        public static void Postfix(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, ref IntVec3 foundCell, ref bool __result)
        {
            // 基类已设置 PassThroughOnly，核心建筑可通行，不需要特殊处理
        }
    }
}

