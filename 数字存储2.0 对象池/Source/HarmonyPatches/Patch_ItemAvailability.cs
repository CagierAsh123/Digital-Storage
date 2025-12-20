using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Services;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 补丁：让 ItemAvailability.ThingsAvailableAnywhere 也检查核心中的材料（包括预留物品和虚拟存储）
    /// </summary>
    [HarmonyPatch(typeof(ItemAvailability), "ThingsAvailableAnywhere")]
    public static class Patch_ItemAvailability_ThingsAvailableAnywhere
    {
        public static bool Prefix(ref bool __result, ThingDef need, int amount, Pawn pawn, ItemAvailability __instance)
        {
            // 检查 Pawn 是否有访问权限（芯片或接口）
            bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
            bool hasInterface = HasAccessibleInterface(pawn.Map);
            
            if (!hasChip && !hasInterface)
            {
                // 没有访问权限，使用原版逻辑
                return true;
            }

            // 获取全局游戏组件
            Game game = Current.Game;
            DigitalStorageGameComponent gameComp = (game != null) ? game.GetComponent<DigitalStorageGameComponent>() : null;
            if (gameComp == null)
            {
                // 没有游戏组件，使用原版逻辑
                return true;
            }

            // 先统计地图上的材料（原版逻辑）
            int totalAvailable = 0;
            Map map = pawn.Map;
            if (map != null)
            {
                List<Thing> list = map.listerThings.ThingsOfDef(need);
                for (int i = 0; i < list.Count; i++)
                {
                    if (!list[i].IsForbidden(pawn))
                    {
                        totalAvailable += list[i].stackCount;
                    }
                }
            }

            // 再统计核心中的材料（包括预留物品和虚拟存储）
            // 优先在当前地图查找
            List<Building_StorageCore> mapCores = gameComp.GetCoresOnMap(map);
            foreach (Building_StorageCore core in mapCores)
            {
                if (core == null || !core.Spawned || !core.Powered)
                {
                    continue;
                }

                // GetItemCount 已经包括预留物品和虚拟存储
                int coreCount = core.GetItemCount(need, null);
                totalAvailable += coreCount;
            }

            // 当前地图没有时，再遍历其他地图
            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered)
                {
                    continue;
                }

                // 跳过已查找的地图
                if (map != null && core.Map == map)
                {
                    continue;
                }

                // GetItemCount 已经包括预留物品和虚拟存储
                int coreCount = core.GetItemCount(need, null);
                totalAvailable += coreCount;
            }

            // 设置结果并跳过原版方法
            __result = totalAvailable >= amount;
            
            // 返回 false 跳过原版方法
            return false;
        }

        /// <summary>
        /// 检查是否有可访问的激活输出接口
        /// </summary>
        private static bool HasAccessibleInterface(Map map)
        {
            if (map == null)
            {
                return false;
            }

            DigitalStorageMapComponent mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null)
            {
                return false;
            }

            foreach (Building_OutputInterface iface in mapComp.GetAllOutputInterfaces())
            {
                if (iface != null && iface.Spawned && iface.IsActive && iface.BoundCore != null && iface.BoundCore.Powered)
                {
                    return true;
                }
            }

            return false;
        }
    }
}

