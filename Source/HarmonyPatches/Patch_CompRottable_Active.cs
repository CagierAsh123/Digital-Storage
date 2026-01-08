using HarmonyLib;
using RimWorld;
using DigitalStorage.Components;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 阻止存储核心上的物品腐烂
    /// </summary>
    [HarmonyPatch(typeof(CompRottable), "Active", MethodType.Getter)]
    public static class Patch_CompRottable_Active
    {
        public static bool Prefix(CompRottable __instance, ref bool __result)
        {
            Thing thing = __instance.parent;
            if (thing == null || !thing.Spawned) return true;

            // 检查物品是否在存储核心上
            SlotGroup slotGroup = thing.Position.GetSlotGroup(thing.Map);
            if (slotGroup?.parent is Building_StorageCore)
            {
                __result = false;  // 不腐烂
                return false;      // 跳过原方法
            }

            return true;  // 继续原方法
        }
    }
}

