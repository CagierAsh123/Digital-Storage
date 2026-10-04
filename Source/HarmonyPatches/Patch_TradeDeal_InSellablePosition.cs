using System.Collections.Generic;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    // 核心内容物需要额外白名单, 但仍须保留原版位置规则
    [HarmonyPatch(typeof(TradeDeal), "InSellablePosition")]
    internal static class Patch_TradeDeal_InSellablePosition
    {
        private static bool Prefix(Thing t, out string reason, ref bool __result)
        {
            Building_StorageCore core = t.ParentHolder as Building_StorageCore;
            if (t.Spawned || core == null)
            {
                reason = null;
                return true;
            }

            if (!core.IsUsableNow || !core.HaulSourceEnabled)
            {
                reason = null;
                __result = false;
                return false;
            }

            IntVec3 positionHeld = t.PositionHeld;
            Map mapHeld = t.MapHeld;
            Room room = t.SpawnedParentOrMe.GetRoom();

            if (mapHeld == null || positionHeld.Fogged(mapHeld))
            {
                reason = null;
                __result = false;
                return false;
            }

            if (room != null)
            {
                int num = GenRadial.NumCellsInRadius(6.9f);
                for (int i = 0; i < num; i++)
                {
                    IntVec3 intVec = positionHeld + GenRadial.RadialPattern[i];
                    if (!intVec.InBounds(mapHeld) || intVec.GetRoom(mapHeld) != room)
                    {
                        continue;
                    }
                    List<Thing> thingList = intVec.GetThingList(mapHeld);
                    for (int j = 0; j < thingList.Count; j++)
                    {
                        if (thingList[j].PreventPlayerSellingThingsNearby(out reason))
                        {
                            __result = false;
                            return false;
                        }
                    }
                }
            }

            reason = null;
            __result = true;
            return false;
        }
    }
}
