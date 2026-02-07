using System;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [HarmonyPatch(typeof(Thing), "DrawGUIOverlay")]
    public static class Patch_Thing_DrawGUIOverlay
    {
        public static bool Prefix(Thing __instance)
        {
            bool flag = __instance.def.category != ThingCategory.Item;
            bool flag2;
            if (flag)
            {
                flag2 = true;
            }
            else
            {
                bool flag3 = __instance.Map == null;
                if (flag3)
                {
                    flag2 = true;
                }
                else
                {
                    Building_StorageCore core = GridsUtility.GetFirstThing<Building_StorageCore>(__instance.Position, __instance.Map);
                    bool flag4 = core != null && core.Spawned && core.Powered;
                    if (flag4)
                    {
                        SlotGroup slotGroup = StoreUtility.GetSlotGroup(__instance.Position, __instance.Map);
                        bool flag5 = slotGroup != null && slotGroup.parent == core;
                        if (flag5)
                        {
                            return false;
                        }
                    }

                    flag2 = true;
                }
            }
            return flag2;
        }
    }
}

