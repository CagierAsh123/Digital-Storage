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
    /// 补丁：让 ResourceCounter.GetCount 也统计核心中的材料（包括预留物品和虚拟存储）
    /// </summary>
    [HarmonyPatch(typeof(ResourceCounter), "GetCount")]
    public static class Patch_ResourceCounter_GetCount
    {
        public static void Postfix(ref int __result, ThingDef rDef, ResourceCounter __instance)
        {
            // 如果资源类型不计数，跳过
            if (rDef.resourceReadoutPriority == ResourceCountPriority.Uncounted)
            {
                return;
            }

            // 获取全局游戏组件
            Game game = Current.Game;
            DigitalStorageGameComponent gameComp = (game != null) ? game.GetComponent<DigitalStorageGameComponent>() : null;
            if (gameComp == null)
            {
                return;
            }

            // 统计所有核心中的该资源数量（GetItemCount 已经包括预留物品和虚拟存储）
            int totalInCores = 0;

            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered)
                {
                    continue;
                }

                totalInCores += core.GetItemCount(rDef, null);
            }

            // 将核心中的数量加到原版结果中
            if (totalInCores > 0)
            {
                __result += totalInCores;
            }
        }
    }

    /// <summary>
    /// 补丁：让 ResourceCounter.AllCountedAmounts 也包含核心中的材料
    /// </summary>
    [HarmonyPatch(typeof(ResourceCounter), "AllCountedAmounts", MethodType.Getter)]
    public static class Patch_ResourceCounter_AllCountedAmounts
    {
        // 缓存机制：减少每秒60次的字典分配（UI资源面板高频调用）
        private static Dictionary<ThingDef, int> _cachedAllCountedAmounts = new Dictionary<ThingDef, int>();
        private static int _cacheFrame = -1;
        private const int CACHE_UPDATE_INTERVAL = 30; // 每30帧更新一次缓存（0.5秒）

        public static void Postfix(ref Dictionary<ThingDef, int> __result, ResourceCounter __instance)
        {
            int currentFrame = Find.TickManager.TicksGame;

            // 缓存机制：每30帧重新计算一次，避免每秒60次的字典分配
            if (currentFrame - _cacheFrame >= CACHE_UPDATE_INTERVAL || _cachedAllCountedAmounts.Count == 0)
            {
                _cachedAllCountedAmounts.Clear();
                _cacheFrame = currentFrame;

                // 获取全局游戏组件
                Game game = Current.Game;
                DigitalStorageGameComponent gameComp = (game != null) ? game.GetComponent<DigitalStorageGameComponent>() : null;
                if (gameComp == null)
                {
                    return;
                }

                // 创建一个新字典，包含原版结果和核心中的材料
                Dictionary<ThingDef, int> newResult = new Dictionary<ThingDef, int>(__result);

                // 统计所有核心中的材料（GetItemCount 已经包括预留物品和虚拟存储）
                foreach (Building_StorageCore core in gameComp.GetAllCores())
                {
                    if (core == null || !core.Spawned || !core.Powered)
                    {
                        continue;
                    }

                    // 遍历虚拟存储
                    foreach (var item in core.GetAllStoredItems())
                    {
                        if (item.def.resourceReadoutPriority == ResourceCountPriority.Uncounted)
                        {
                            continue;
                        }

                        if (newResult.ContainsKey(item.def))
                        {
                            newResult[item.def] += item.stackCount;
                        }
                        else
                        {
                            newResult[item.def] = item.stackCount;
                        }
                    }

                    // 也统计预留物品
                    SlotGroup slotGroup = core.GetSlotGroup();
                    if (slotGroup != null)
                    {
                        foreach (Thing thing in slotGroup.HeldThings)
                        {
                            if (thing != null && thing.Spawned && thing.def.resourceReadoutPriority != ResourceCountPriority.Uncounted)
                            {
                                if (newResult.ContainsKey(thing.def))
                                {
                                    newResult[thing.def] += thing.stackCount;
                                }
                                else
                                {
                                    newResult[thing.def] = thing.stackCount;
                                }
                            }
                        }
                    }
                }

                _cachedAllCountedAmounts = newResult;
            }

            // 返回缓存结果（避免重复分配）
            __result = _cachedAllCountedAmounts;
        }
    }
}

