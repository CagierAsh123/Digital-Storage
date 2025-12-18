using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.Components;
using DigitalStorage.Data;
using DigitalStorage.Services;
using DigitalStorage.Settings;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 蓝图建造材料查找：注入虚拟 Thing
    /// 核心逻辑：原版找不到材料时，从虚拟存储查找并创建虚拟 Thing
    /// 基于 2.0 架构：使用虚拟 Thing 注入，不使用 GhostMarker
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "ResourceDeliverJobFor")]
    public static class Patch_WorkGiver_ConstructDeliverResources_V2
    {
        public static void Postfix(
            ref Job __result,
            WorkGiver_ConstructDeliverResources __instance,
            Pawn pawn,
            IConstructible c,
            bool canRemoveExistingFloorUnderNearbyNeeders,
            bool forced)
        {
            // 如果已经找到工作，不需要处理
            if (__result != null)
            {
                return;
            }

            // 获取组件
            Game game = Current.Game;
            DigitalStorageGameComponent gameComp = game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            // 检查是否有访问权限（有芯片或有接口都可以）
            bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
            bool hasInterface = HasAccessibleInterface(pawn.Map, gameComp);
            
            if (!hasChip && !hasInterface)
            {
                return;
            }

            // 遍历所需材料
            foreach (ThingDefCountClass need in c.TotalMaterialCost())
            {
                int countNeeded = GetCountNeeded(c, need.thingDef, pawn, forced);
                if (countNeeded <= 0)
                {
                    continue;
                }

                // 在虚拟存储中查找材料（优先检查预留物品，然后检查虚拟存储）
                ThingDef foundDef = need.thingDef;
                ThingDef foundStuff = null;
                Building_StorageCore foundCore = null;
                Thing foundReservedThing = null;
                int reservedCount = 0;
                int virtualCount = 0;
                bool useReserved = false;

                // 遍历所有核心，查找匹配的材料
                foreach (Building_StorageCore core in gameComp.GetAllCores())
                {
                    if (core == null || !core.Spawned || !core.Powered)
                    {
                        continue;
                    }

                    // 先检查预留物品
                    Thing reservedThing = core.FindReservedItem(foundDef, null);
                    if (reservedThing != null && reservedThing.Spawned)
                    {
                        reservedCount = reservedThing.stackCount;
                        if (reservedCount >= countNeeded)
                        {
                            // 预留物品足够，优先使用预留物品
                            foundCore = core;
                            foundReservedThing = reservedThing;
                            foundStuff = reservedThing.Stuff;
                            useReserved = true;
                            break;
                        }
                        else
                        {
                            // ⚠️ 预留物品不够，记录预留物品信息，查找虚拟存储补足
                            foundCore = core;
                            foundReservedThing = reservedThing;
                            foundStuff = reservedThing.Stuff;
                            // 继续查找虚拟存储，看是否能补足
                        }
                    }

                    // 如果预留物品不够或没有预留物品，查找虚拟存储
                    if (!useReserved)
                    {
                        // 查找虚拟存储
                        foreach (StoredItemData itemData in core.GetAllStoredItems())
                        {
                            if (itemData.def == null || itemData.stackCount <= 0)
                            {
                                continue;
                            }

                            // 检查是否匹配
                            if (itemData.def != foundDef)
                            {
                                continue;
                            }

                            // 检查数量是否足够（如果预留不够，需要完整数量；如果没有预留，也需要完整数量）
                            int neededFromVirtual = foundReservedThing != null ? countNeeded : countNeeded;
                            if (itemData.stackCount >= neededFromVirtual)
                            {
                                foundDef = itemData.def;
                                foundStuff = itemData.stuffDef;
                                foundCore = core;
                                virtualCount = neededFromVirtual;
                                break;
                            }
                        }
                    }

                    if (foundCore != null && (useReserved || virtualCount > 0))
                    {
                        break;
                    }
                }

                // ⚠️ 如果没有预留物品，不创建Job，让原版逻辑处理
                if (foundReservedThing == null)
                {
                    // 没有预留物品，说明玩家没有这个东西，没必要创建Job
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Message($"[数字存储] ResourceDeliverJobFor: 没有预留物品，不创建Job，让原版逻辑处理, 需要={countNeeded} {foundDef?.label ?? "null"}");
                    }
                    continue;  // 继续查找下一个材料
                }

                // 如果找不到匹配的材料或核心，继续查找下一个材料
                if (foundCore == null || foundDef == null)
                {
                    continue;
                }
                
                // ⚠️ 如果预留物品不够，但虚拟存储也不够，不创建Job
                if (!useReserved && foundReservedThing != null && virtualCount == 0)
                {
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Message($"[数字存储] ResourceDeliverJobFor: 预留物品不够且虚拟存储也不够，不创建Job, 预留={foundReservedThing.stackCount}, 需要={countNeeded}");
                    }
                    continue;  // 继续查找下一个材料
                }

                int countToTake = Math.Min(countNeeded, pawn.carryTracker.MaxStackSpaceEver(foundDef));
                Job job = JobMaker.MakeJob(JobDefOf.HaulToContainer);
                job.targetB = (Thing)c;  // 蓝图
                job.count = countToTake;
                job.haulMode = HaulMode.ToContainer;

                // ⚠️ 混合方案：优先使用预留物品提供 Position
                if (useReserved && foundReservedThing != null)
                {
                    // 情况1：预留物品足够，直接使用预留物品作为 Job 目标（原版逻辑处理）
                    job.targetA = foundReservedThing;
                    
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Message($"[数字存储] ResourceDeliverJobFor: 使用预留物品作为Job目标（足够）, {foundReservedThing.Label} x{foundReservedThing.stackCount}, 需要={countNeeded}");
                    }
                }
                else if (foundReservedThing != null && virtualCount > 0)
                {
                    // 情况2：预留物品不够，使用预留物品作为 Job 目标（提供position），在 targetC 中存储需要从虚拟存储提取的完整数量
                    job.targetA = foundReservedThing;
                    
                    // 在 targetC 中存储需要从虚拟存储提取的完整数量
                    Thing infoCarrier = ThingMaker.MakeThing(foundDef, foundStuff);
                    infoCarrier.stackCount = countNeeded;  // 完整数量，不是补足数量
                    job.targetC = infoCarrier;
                    
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Message($"[数字存储] ResourceDeliverJobFor: 使用预留物品作为Job目标（不够，需从虚拟存储提取完整数量）, 预留={foundReservedThing.stackCount}, 需要={countNeeded}");
                    }
                }
                // 情况3：没有预留物品，不创建Job（已在上面处理，continue）

                __result = job;
                
                if (DigitalStorageSettings.enableDebugLog)
                {
                    string sourceType = useReserved ? "预留物品" : (foundReservedThing != null ? "预留+虚拟" : "虚拟存储");
                    Log.Message($"[数字存储] ResourceDeliverJobFor: 为建造任务创建Job，材料来源: {sourceType}");
                }
                return;
            }
        }

        /// <summary>
        /// 获取生成位置（有芯片→蓝图位置，无芯片→输出接口位置）
        /// </summary>
        private static IntVec3 GetSpawnPosition(Pawn pawn, Building_StorageCore core, IConstructible c, Map map)
        {
            bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
            
            if (hasChip)
            {
                // 有芯片：使用蓝图位置
                if (c is Thing thing)
                {
                    return thing.Position;
                }
                return pawn.Position;
            }
            else
            {
                // 无芯片：查找最近的输出接口
                Building_OutputInterface nearestInterface = FindNearestOutputInterface(map, core);
                if (nearestInterface != null && nearestInterface.Spawned)
                {
                    return nearestInterface.Position;
                }
                
                // 没有接口，使用蓝图位置
                if (c is Thing thing)
                {
                    return thing.Position;
                }
                return pawn.Position;
            }
        }

        private static int GetCountNeeded(IConstructible c, ThingDef def, Pawn pawn, bool forced)
        {
            if (forced)
            {
                return c.ThingCountNeeded(def);
            }

            IHaulEnroute haulEnroute = c as IHaulEnroute;
            if (haulEnroute == null)
            {
                return c.ThingCountNeeded(def);
            }

            return haulEnroute.GetSpaceRemainingWithEnroute(def, pawn);
        }

        /// <summary>
        /// 查找最近的输出接口
        /// </summary>
        private static Building_OutputInterface FindNearestOutputInterface(Map map, Building_StorageCore core)
        {
            if (map == null)
            {
                return null;
            }

            DigitalStorageMapComponent mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null)
            {
                return null;
            }

            Building_OutputInterface nearest = null;
            float nearestDist = float.MaxValue;

            foreach (Building_OutputInterface iface in mapComp.GetAllOutputInterfaces())
            {
                if (iface == null || !iface.Spawned || !iface.IsActive || iface.Map != map)
                {
                    continue;
                }

                if (iface.BoundCore != core)
                {
                    continue;
                }

                float dist = (iface.Position - map.Center).LengthHorizontalSquared;
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = iface;
                }
            }

            return nearest;
        }

        /// <summary>
        /// 检查是否有可访问的激活输出接口（支持跨地图）
        /// </summary>
        private static bool HasAccessibleInterface(Map map, DigitalStorageGameComponent gameComp)
        {
            if (map == null || gameComp == null)
            {
                return false;
            }

            // 检查当前地图的输出接口
            DigitalStorageMapComponent mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp != null)
            {
                foreach (Building_OutputInterface iface in mapComp.GetAllOutputInterfaces())
                {
                    if (iface != null && iface.Spawned && iface.IsActive && iface.BoundCore != null && iface.BoundCore.Powered)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}

