using System;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [HarmonyPatch(typeof(Thing), "Print")]
    public static class Patch_Thing_Print
    {
        public static bool Prefix(Thing __instance, SectionLayer layer)
        {
            // 只处理物品
            if (__instance.def.category != ThingCategory.Item)
            {
                return true;
            }

            // 检查地图
            if (__instance.Map == null)
            {
                return true;
            }

            // 获取物品所在的 SlotGroup
            SlotGroup slotGroup = StoreUtility.GetSlotGroup(__instance.Position, __instance.Map);
            if (slotGroup == null)
            {
                return true;
            }

            // 检查是否在存储核心中
            Building_StorageCore core = slotGroup.parent as Building_StorageCore;
            if (core != null && core.Spawned && core.Powered)
            {
                return false; // 隐藏渲染
            }

            // 检查是否在磁盘柜中
            Building_DiskCabinet cabinet = slotGroup.parent as Building_DiskCabinet;
            if (cabinet != null && cabinet.Spawned)
            {
                return false; // 隐藏渲染
            }

            return true; // 正常渲染
        }
    }
}

