using System;
using DigitalStorage.Components;
using DigitalStorage.Services;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// Achtung 兼容性补丁：让预订系统认为跨地图核心中的物品可以预订
    /// </summary>
    [HarmonyPatch(typeof(ReservationManager), "CanReserve")]
    public static class Patch_ReservationManager_CanReserve_Achtung
    {
        public static void Postfix(
            ref bool __result,
            ReservationManager __instance,
            Pawn claimant,
            LocalTargetInfo target,
            int maxPawns,
            int stackCount,
            ReservationLayerDef layer,
            bool ignoreOtherReservations)
        {
            // 如果已经可以预订，不需要处理
            if (__result)
            {
                return;
            }

            // 只处理物品目标
            if (!target.HasThing)
            {
                return;
            }

            Thing thing = target.Thing;
            if (thing == null || claimant == null)
            {
                return;
            }

            // 检查 Pawn 是否有终端芯片
            if (!PawnStorageAccess.HasTerminalImplant(claimant))
            {
                return;
            }

            // 如果物品在当前地图，使用正常逻辑
            if (thing.Map == claimant.Map)
            {
                return;
            }

            // 获取游戏组件
            Game game = Current.Game;
            DigitalStorageGameComponent gameComp = (game != null) ? game.GetComponent<DigitalStorageGameComponent>() : null;
            if (gameComp == null)
            {
                return;
            }

            // 查找物品所在的核心
            Building_StorageCore core = gameComp.FindCoreWithItem(thing);
            if (core == null || !core.Powered)
            {
                return;
            }

            // 检查物品是否在核心的存储区域内
            SlotGroup slotGroup = core.GetSlotGroup();
            if (slotGroup == null)
            {
                return;
            }

            bool itemInCore = false;
            foreach (Thing heldThing in slotGroup.HeldThings)
            {
                if (heldThing == thing)
                {
                    itemInCore = true;
                    break;
                }
            }

            if (!itemInCore)
            {
                return;
            }

            // 物品在跨地图的核心中，允许预订
            __result = true;
        }
    }
}

