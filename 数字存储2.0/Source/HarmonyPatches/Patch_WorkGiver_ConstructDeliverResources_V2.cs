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
                            // 预留物品不够，记录信息，继续查找虚拟存储补足
                            foundCore = core;
                            foundReservedThing = reservedThing;
                            foundStuff = reservedThing.Stuff;
                            virtualCount = countNeeded - reservedCount;
                        }
                    }

                    // 如果预留物品不够，查找虚拟存储补足
                    if (!useReserved && foundCore != null && virtualCount > 0)
                    {
                        bool foundVirtualEnough = false;
                        // 遍历虚拟存储中的物品
                        foreach (StoredItemData itemData in core.GetAllStoredItems())
                        {
                            if (itemData.def == null || itemData.stackCount <= 0)
                            {
                                continue;
                            }

                            // 检查是否匹配预留物品的类型
                            if (foundDef != null && (itemData.def != foundDef || itemData.stuffDef != foundStuff))
                            {
                                continue;
                            }

                            // 检查数量是否足够补足
                            if (itemData.stackCount >= virtualCount)
                            {
                                foundVirtualEnough = true;
                                break;
                            }
                        }
                        
                        // 如果虚拟存储不够补足，重置 foundCore，继续查找其他核心
                        if (!foundVirtualEnough)
                        {
                            foundCore = null;
                            foundReservedThing = null;
                            virtualCount = 0;
                        }
                    }
                    else if (!useReserved)
                    {
                        // 没有预留物品，查找虚拟存储
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

                            // 检查数量是否足够
                            if (itemData.stackCount >= countNeeded)
                            {
                                foundDef = itemData.def;
                                foundStuff = itemData.stuffDef;
                                foundCore = core;
                                virtualCount = countNeeded;
                                break;
                            }
                        }
                    }

                    if (foundCore != null && (useReserved || virtualCount > 0))
                    {
                        break;
                    }
                }

                // 如果找不到匹配的材料，继续查找下一个材料
                if (foundCore == null || foundDef == null)
                {
                    continue;
                }

                // 创建临时 Thing（虚拟材料）
                Thing tempThing = ThingMaker.MakeThing(foundDef, foundStuff);
                tempThing.stackCount = countNeeded;

                // 设置 position：如果使用预留物品，使用预留物品的 position；否则使用蓝图位置
                IntVec3 spawnPos;
                if (useReserved && foundReservedThing != null)
                {
                    spawnPos = foundReservedThing.Position;
                }
                else
                {
                    // 使用蓝图位置（或最近的输出接口位置）
                    spawnPos = GetSpawnPosition(pawn, foundCore, c, pawn.Map);
                }
                tempThing.Position = spawnPos;

                // 必须 Spawn 到地图上，否则 CanReserve 会失败
                GenSpawn.Spawn(tempThing, spawnPos, pawn.Map, WipeMode.Vanish);

                // 标记为虚拟材料
                CompVirtualIngredient comp = null;
                ThingWithComps thingWithComps = tempThing as ThingWithComps;
                if (thingWithComps != null)
                {
                    comp = thingWithComps.GetComp<CompVirtualIngredient>();
                }
                if (comp == null && thingWithComps != null)
                {
                    // 使用反射直接添加 Comp（参考 Vehicle Framework 的方法）
                    try
                    {
                        comp = new CompVirtualIngredient();
                        comp.parent = thingWithComps;
                        comp.Initialize(new CompProperties_VirtualIngredient());
                        
                        // 使用反射访问 comps 字段
                        var compsField = typeof(ThingWithComps).GetField("comps", BindingFlags.NonPublic | BindingFlags.Instance);
                        if (compsField != null)
                        {
                            var compsList = compsField.GetValue(thingWithComps) as List<ThingComp>;
                            if (compsList == null)
                            {
                                compsList = new List<ThingComp>();
                                compsField.SetValue(thingWithComps, compsList);
                            }
                            compsList.Add(comp);
                        }
                    }
                    catch (Exception ex)
                    {
                        if (DigitalStorageSettings.enableDebugLog)
                        {
                            Log.Warning($"[数字存储] 无法添加 CompVirtualIngredient: {ex.Message}");
                        }
                    }
                }
                
                if (comp != null)
                {
                    comp.SetVirtual(true);
                    comp.SetSourceCore(foundCore);
                    comp.SetSourceMap(foundCore.Map);
                    
                    // 设置预留物品信息
                    if (useReserved && foundReservedThing != null)
                    {
                        comp.SetFromReserved(true, foundReservedThing);
                    }
                    else if (foundReservedThing != null)
                    {
                        // 需要补足：标记预留物品和虚拟数量
                        comp.SetFromReserved(false, foundReservedThing);
                    }
                }

                // 创建搬运任务，目标是蓝图
                int countToTake = Math.Min(countNeeded, pawn.carryTracker.MaxStackSpaceEver(foundDef));
                
                Job job = JobMaker.MakeJob(JobDefOf.HaulToContainer);
                job.targetA = tempThing;  // 材料（虚拟 Thing）
                job.targetB = (Thing)c;  // 蓝图
                job.count = countToTake;
                job.haulMode = HaulMode.ToContainer;

                __result = job;
                
                if (DigitalStorageSettings.enableDebugLog)
                {
                    string sourceType = useReserved ? "预留物品" : "虚拟存储";
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

