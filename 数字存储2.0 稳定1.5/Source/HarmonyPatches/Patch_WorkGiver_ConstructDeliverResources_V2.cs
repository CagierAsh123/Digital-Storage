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
    /// 蓝图建造材料查找：先补货，再使用物品传送仪逻辑
    /// 核心逻辑：
    /// 1. 原版找不到材料时，检查虚拟存储
    /// 2. 如果预留物品不够，立即补货到预留位置
    /// 3. 使用补货后的真实物品创建Job，套用物品传送仪逻辑
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

            // 检查pawn是否有访问权限
            bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
            bool hasInterface = HasAccessibleInterface(pawn.Map, gameComp);

            if (!hasChip && !hasInterface)
            {
                // 无访问权限，让原版逻辑处理
                return;
            }

            // 为第一个可处理的材料创建job（避免创建多个job）
            foreach (ThingDefCountClass need in c.TotalMaterialCost())
            {
                int countNeeded = GetCountNeeded(c, need.thingDef, pawn, forced);
                if (countNeeded <= 0)
                {
                    continue;
                }

                // 查找这个材料是否有来源
                Building_StorageCore materialCore = null;
                Thing reservedThing = null;

                foreach (Building_StorageCore core in gameComp.GetAllCores())
                {
                    if (core == null || !core.Spawned || !core.Powered)
                    {
                        continue;
                    }

                    // 检查预留物品
                    reservedThing = core.FindReservedItem(need.thingDef, null);
                    if (reservedThing != null && reservedThing.Spawned)
                    {
                        materialCore = core;
                        break;
                    }

                    // 检查虚拟存储
                    foreach (StoredItemData itemData in core.GetAllStoredItems())
                    {
                        if (itemData.def == need.thingDef && itemData.stackCount > 0)
                        {
                            materialCore = core;
                            break;
                        }
                    }

                    if (materialCore != null)
                    {
                        break;
                    }
                }

                // 如果找到核心，先补货再创建job
                if (materialCore != null)
                {
                    // 确定补货位置
                    IntVec3 replenishPos = GetReplenishPosition(pawn, materialCore, c, reservedThing);
                    
                    // 限制补货数量为pawn一次能拿的数量
                    int maxCanCarry = pawn.carryTracker.MaxStackSpaceEver(need.thingDef);
                    int actualReplenishCount = Math.Min(countNeeded, maxCanCarry);
                    
                    // 补货到需要的数量
                    Thing replenishedThing = ReplenishToNeededCount(materialCore, need.thingDef, null, actualReplenishCount, replenishPos, pawn.Map, reservedThing);
                    
                    if (replenishedThing != null && replenishedThing.Spawned)
                    {
                        // 确定传送目标位置
                        IntVec3? teleportPos = DetermineTeleportPosition(pawn, materialCore, c, hasChip, hasInterface);
                        
                        // 如果需要传送且位置不同，执行传送
                        if (teleportPos.HasValue && teleportPos.Value != replenishedThing.Position)
                        {
                            replenishedThing = TeleportReplenishedItemToTarget(replenishedThing, teleportPos.Value, pawn.Map);
                            
                            if (replenishedThing == null)
                            {
                                if (DigitalStorageSettings.enableDebugLog)
                                {
                                    Log.Warning($"[数字存储] ResourceDeliverJobFor: 传送失败");
                                }
                                continue;
                            }
                            
                            if (DigitalStorageSettings.enableDebugLog)
                            {
                                Log.Message($"[数字存储] ResourceDeliverJobFor: 补货并传送完成, 材料={need.thingDef.label}, 数量={replenishedThing.stackCount}, 从 {replenishPos} 传送到 {teleportPos.Value}");
                            }
                        }
                        else if (DigitalStorageSettings.enableDebugLog)
                        {
                            Log.Message($"[数字存储] ResourceDeliverJobFor: 补货完成，无需传送, 材料={need.thingDef.label}, 数量={replenishedThing.stackCount}, 位置={replenishedThing.Position}");
                        }

                        // 使用传送后的物品创建Job
                        __result = CreateTeleportJobForRealThing(pawn, c, replenishedThing, actualReplenishCount);
                        return;
                    }
                    else
                    {
                        if (DigitalStorageSettings.enableDebugLog)
                        {
                            Log.Warning($"[数字存储] ResourceDeliverJobFor: 补货失败，无法创建Job, 材料={need.thingDef.label}, 需要={countNeeded}");
                        }
                    }
                }
            }

            // 如果没有找到任何可处理的材料，让原版逻辑处理
            return;
        }


        /// <summary>
        /// 获取补货位置（预留物品在核心位置）
        /// </summary>
        private static IntVec3 GetReplenishPosition(Pawn pawn, Building_StorageCore core, IConstructible c, Thing reservedThing)
        {
            // 优先使用预留物品的位置
            if (reservedThing != null && reservedThing.Spawned)
            {
                return reservedThing.Position;
            }

            // 没有预留物品，补货到核心位置
            return core.Position;
        }

        /// <summary>
        /// 补货到需要的数量
        /// </summary>
        private static Thing ReplenishToNeededCount(Building_StorageCore core, ThingDef def, ThingDef stuff, int needed, IntVec3 position, Map map, Thing existingReserved)
        {
            if (core == null || def == null || needed <= 0 || map == null)
            {
                return null;
            }

            int currentCount = existingReserved?.stackCount ?? 0;
            int needMore = needed - currentCount;

            if (needMore <= 0)
            {
                // 预留物品已经足够，直接返回
                return existingReserved;
            }

            // 从虚拟存储提取需要的数量
            Thing extracted = core.ExtractItem(def, needMore, stuff);
            if (extracted == null)
            {
                if (DigitalStorageSettings.enableDebugLog)
                {
                    Log.Warning($"[数字存储] ReplenishToNeededCount: 虚拟存储不足，无法补货, 需要 {def.label} x{needMore}");
                }
                return existingReserved; // 返回现有的预留物品
            }

            // 如果有现有预留物品，合并堆栈
            if (existingReserved != null && existingReserved.Spawned)
            {
                bool absorbed = existingReserved.TryAbsorbStack(extracted, false);
                
                if (absorbed)
                {
                    // 合并成功
                    // TryAbsorbStack的行为：
                    // - 如果完全吸收，extracted会被销毁
                    // - 如果部分吸收，extracted会保留剩余数量
                    // 安全检查：只有在extracted还存在且有剩余时才spawn
                    try
                    {
                        if (extracted != null && !extracted.Destroyed && extracted.stackCount > 0)
                        {
                            GenSpawn.Spawn(extracted, existingReserved.Position, map, WipeMode.Vanish);
                        }
                    }
                    catch
                    {
                        // extracted已被销毁，忽略
                    }
                    return existingReserved;
                }
                else
                {
                    // 无法合并（堆叠限制），在预留物品位置spawn新物品
                    GenSpawn.Spawn(extracted, existingReserved.Position, map, WipeMode.Vanish);
                    return existingReserved; // 返回预留物品（提取的物品在旁边）
                }
            }
            else
            {
                // 没有预留物品，在指定位置spawn
                GenSpawn.Spawn(extracted, position, map, WipeMode.Vanish);
                return extracted;
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
        /// 确定传送目标位置
        /// </summary>
        private static IntVec3? DetermineTeleportPosition(Pawn pawn, Building_StorageCore core, IConstructible c, bool hasChip, bool hasInterface)
        {
            // 获取蓝图位置
            IntVec3 blueprintPos = (c is Thing blueprint && blueprint.Spawned) ? blueprint.Position : core.Position;
            
            if (hasChip)
            {
                // 有芯片：传送到蓝图位置
                if (c is Thing thing && thing.Spawned)
                {
                    return thing.Position;
                }
                return null;
            }
            else if (hasInterface)
            {
                // 无芯片：比较接口和核心到蓝图的距离
                Building_OutputInterface nearestInterface = FindNearestOutputInterface(pawn.Map, core, blueprintPos);
                if (nearestInterface != null && nearestInterface.Spawned)
                {
                    float interfaceDistance = (nearestInterface.Position - blueprintPos).LengthHorizontalSquared;
                    float coreDistance = (core.Position - blueprintPos).LengthHorizontalSquared;
                    
                    if (interfaceDistance < coreDistance)
                    {
                        // 接口更近，传送到接口位置
                        return nearestInterface.Position;
                    }
                    // 核心更近，不传送（让pawn走过去）
                    return null;
                }
            }
            
            // 无接口，不传送
            return null;
        }

        /// <summary>
        /// 传送补货后的物品到目标位置
        /// </summary>
        private static Thing TeleportReplenishedItemToTarget(Thing item, IntVec3 targetPos, Map map)
        {
            if (item == null || !item.Spawned || map == null)
            {
                return null;
            }

            IntVec3 originalPos = item.Position;
            
            // DeSpawn原物品
            item.DeSpawn(0);
            
            // Spawn到目标位置
            Thing spawned = GenSpawn.Spawn(item, targetPos, map, 0);
            
            // 添加闪电效果
            FleckMaker.ThrowLightningGlow(spawned.DrawPos, map, 0.5f);
            
            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] TeleportReplenishedItemToTarget: 传送完成, {spawned.Label} x{spawned.stackCount} 从 {originalPos} 到 {targetPos}");
            }
            
            return spawned;
        }

        /// <summary>
        /// 查找离蓝图最近的输出接口
        /// </summary>
        private static Building_OutputInterface FindNearestOutputInterface(Map map, Building_StorageCore core, IntVec3 blueprintPos)
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

                float dist = (iface.Position - blueprintPos).LengthHorizontalSquared;
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = iface;
                }
            }

            return nearest;
        }

        /// <summary>
        /// 为真实物品创建传送Job（类似物品传送仪逻辑）
        /// </summary>
        private static Job CreateTeleportJobForRealThing(Pawn pawn, IConstructible c, Thing realThing, int countNeeded)
        {
            if (realThing == null || !realThing.Spawned)
            {
                return null;
            }

            int countToTake = Math.Min(countNeeded, pawn.carryTracker.MaxStackSpaceEver(realThing.def));
            Job job = JobMaker.MakeJob(JobDefOf.HaulToContainer);
            job.targetA = realThing;
            job.targetB = (Thing)c;
            job.count = countToTake;

            // 预约真实物品
            if (!pawn.Map.reservationManager.Reserve(pawn, job, realThing, 1, -1, null, false, false))
            {
                if (DigitalStorageSettings.enableDebugLog)
                {
                    Log.Warning($"[数字存储] CreateTeleportJobForRealThing: 无法预约物品, {realThing.Label}");
                }
                return null;
            }

            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] CreateTeleportJobForRealThing: 创建Job成功, 材料={realThing.Label} x{realThing.stackCount}, 需要={countNeeded}");
            }

            return job;
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