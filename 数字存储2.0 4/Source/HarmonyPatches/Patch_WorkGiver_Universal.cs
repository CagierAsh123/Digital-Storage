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
    /// 通用 WorkGiver 支持：自动为所有 WorkGiver 提供虚拟存储材料查找
    /// Hook WorkGiver_Scanner.JobOnThing，当找不到材料时从虚拟存储创建虚拟 Thing
    /// 基于 2.0 架构：使用虚拟 Thing 注入，不使用 GhostMarker
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_Scanner), "JobOnThing")]
    public static class Patch_WorkGiver_Universal
    {
        // 跳过的 WorkGiver（已有专门处理或不需要处理）
        private static readonly HashSet<Type> SkippedWorkGivers = new HashSet<Type>
        {
            typeof(WorkGiver_DoBill),                     // 已有专门 Patch
            typeof(WorkGiver_ConstructDeliverResources),  // 已有专门 Patch
            typeof(WorkGiver_HaulGeneral),                // 搬运不需要
        };

        public static void Postfix(
            ref Job __result,
            WorkGiver_Scanner __instance,
            Pawn pawn,
            Thing t,
            bool forced)
        {
            // 如果已经找到工作，不需要处理
            if (__result != null)
            {
                return;
            }

            // 跳过特定的 WorkGiver
            Type workGiverType = __instance.GetType();
            if (SkippedWorkGivers.Contains(workGiverType))
            {
                return;
            }

            // 基础检查
            if (pawn == null || pawn.Map == null || t == null)
            {
                return;
            }

            // 获取游戏组件
            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            // 检查是否有访问权限（有芯片或有接口都可以）
            bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
            bool hasInterface = HasAccessibleInterface(pawn.Map, gameComp);
            
            if (!hasChip && !hasInterface)
            {
                return; // 回退到原版逻辑
            }

            // 识别 WorkGiver 类型，确定需要的物品
            WorkGiverItemInfo itemInfo = GetRequiredItem(__instance, pawn, t, forced);
            if (itemInfo == null || itemInfo.def == null)
            {
                return; // 回退到原版逻辑
            }

            // 从虚拟存储查找（优先预留物品）
            VirtualItemSource source = FindItemInStorage(gameComp, itemInfo.def, itemInfo.stuff, itemInfo.count);
            if (source == null)
            {
                return; // 回退到原版逻辑
            }

            // 创建虚拟 Thing
            Thing virtualThing = CreateVirtualThing(source, itemInfo, pawn, t, hasChip);
            if (virtualThing == null)
            {
                return; // 回退到原版逻辑
            }

            // 检查可访问性
            if (!CanPawnAccess(pawn, virtualThing))
            {
                if (virtualThing.Spawned)
                {
                    virtualThing.DeSpawn(DestroyMode.Vanish);
                }
                virtualThing.Destroy(DestroyMode.Vanish);
                return; // 回退到原版逻辑
            }

            // 创建 Job
            Job job = CreateJobForWorkGiver(__instance, pawn, t, virtualThing, itemInfo.count);
            if (job != null)
            {
                __result = job;
                
                if (DigitalStorageSettings.enableDebugLog)
                {
                    string sourceType = source.isFromReserved ? "预留物品" : "虚拟存储";
                    Log.Message($"[数字存储] 通用 WorkGiver 为 {workGiverType.Name} 创建了 Job，材料来源: {sourceType}");
                }
            }
            else
            {
                // 创建 Job 失败，销毁虚拟 Thing
                if (virtualThing.Spawned)
                {
                    virtualThing.DeSpawn(DestroyMode.Vanish);
                }
                virtualThing.Destroy(DestroyMode.Vanish);
            }
        }

        /// <summary>
        /// WorkGiver 需要的物品信息
        /// </summary>
        private class WorkGiverItemInfo
        {
            public ThingDef def;
            public ThingDef stuff;
            public int count;
        }

        /// <summary>
        /// 虚拟物品来源（预留或虚拟存储）
        /// </summary>
        private class VirtualItemSource
        {
            public Building_StorageCore core;
            public Thing reservedThing;  // 如果来自预留物品
            public bool isFromReserved;
            public int reservedCount;
            public int virtualCount;
        }

        /// <summary>
        /// 识别 WorkGiver 类型，确定需要的物品
        /// </summary>
        private static WorkGiverItemInfo GetRequiredItem(WorkGiver_Scanner workGiver, Pawn pawn, Thing target, bool forced)
        {
            WorkGiverItemInfo info = new WorkGiverItemInfo();

            // 医疗相关
            if (workGiver is WorkGiver_Tend)
            {
                Pawn patient = target as Pawn;
                if (patient != null)
                {
                    ThingDef bestMedicine = GetBestMedicine(pawn, patient);
                    if (bestMedicine != null)
                    {
                        info.def = bestMedicine;
                        info.stuff = null;
                        info.count = 1;
                        return info;
                    }
                }
            }
            // 加燃料
            else if (workGiver is WorkGiver_Refuel)
            {
                CompRefuelable refuelable = target?.TryGetComp<CompRefuelable>();
                if (refuelable != null && !refuelable.IsFull)
                {
                    info.def = refuelable.Props.fuelFilter.AnyAllowedDef;
                    info.stuff = null;
                    info.count = (int)refuelable.GetFuelCountToFullyRefuel();
                    return info;
                }
            }
            // 填充发酵桶
            else if (workGiver is WorkGiver_FillFermentingBarrel)
            {
                Building_FermentingBarrel barrel = target as Building_FermentingBarrel;
                if (barrel != null && barrel.SpaceLeftForWort > 0)
                {
                    info.def = ThingDefOf.Wort;
                    info.stuff = null;
                    info.count = barrel.SpaceLeftForWort;
                    return info;
                }
            }
            // 修理损坏建筑
            else if (workGiver is WorkGiver_FixBrokenDownBuilding)
            {
                Building building = target as Building;
                if (building != null && building.IsBrokenDown())
                {
                    info.def = ThingDefOf.ComponentIndustrial;
                    info.stuff = null;
                    info.count = 1;
                    return info;
                }
            }

            return null;
        }

        /// <summary>
        /// 从虚拟存储查找物品（优先预留物品，然后虚拟存储补足）
        /// </summary>
        private static VirtualItemSource FindItemInStorage(
            DigitalStorageGameComponent gameComp,
            ThingDef def,
            ThingDef stuff,
            int neededCount)
        {
            if (gameComp == null || def == null)
            {
                return null;
            }

            // 遍历所有核心，查找匹配的物品
            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered)
                {
                    continue;
                }

                // 先检查预留物品
                Thing reservedThing = core.FindReservedItem(def, stuff);
                if (reservedThing != null && reservedThing.Spawned)
                {
                    int reservedCount = reservedThing.stackCount;
                    if (reservedCount >= neededCount)
                    {
                        // 预留物品足够
                        return new VirtualItemSource
                        {
                            core = core,
                            reservedThing = reservedThing,
                            isFromReserved = true,
                            reservedCount = reservedCount,
                            virtualCount = 0
                        };
                    }
                    else
                    {
                        // 预留物品不够，检查虚拟存储补足
                        int virtualCount = neededCount - reservedCount;
                        bool foundVirtualEnough = false;

                        foreach (StoredItemData itemData in core.GetAllStoredItems())
                        {
                            if (itemData.def == null || itemData.stackCount <= 0)
                            {
                                continue;
                            }

                            if (itemData.def != def || itemData.stuffDef != stuff)
                            {
                                continue;
                            }

                            if (itemData.stackCount >= virtualCount)
                            {
                                foundVirtualEnough = true;
                                break;
                            }
                        }

                        if (foundVirtualEnough)
                        {
                            return new VirtualItemSource
                            {
                                core = core,
                                reservedThing = reservedThing,
                                isFromReserved = false,
                                reservedCount = reservedCount,
                                virtualCount = virtualCount
                            };
                        }
                    }
                }
                else
                {
                    // 没有预留物品，查找虚拟存储
                    foreach (StoredItemData itemData in core.GetAllStoredItems())
                    {
                        if (itemData.def == null || itemData.stackCount <= 0)
                        {
                            continue;
                        }

                        if (itemData.def != def || itemData.stuffDef != stuff)
                        {
                            continue;
                        }

                        if (itemData.stackCount >= neededCount)
                        {
                            return new VirtualItemSource
                            {
                                core = core,
                                reservedThing = null,
                                isFromReserved = false,
                                reservedCount = 0,
                                virtualCount = neededCount
                            };
                        }
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 创建虚拟 Thing
        /// </summary>
        private static Thing CreateVirtualThing(
            VirtualItemSource source,
            WorkGiverItemInfo itemInfo,
            Pawn pawn,
            Thing target,
            bool hasChip)
        {
            // 创建临时 Thing
            Thing tempThing = ThingMaker.MakeThing(itemInfo.def, itemInfo.stuff);
            tempThing.stackCount = itemInfo.count;

            // 获取生成位置
            IntVec3 spawnPos = GetSpawnPosition(pawn, target, source.core, hasChip);
            if (!spawnPos.IsValid)
            {
                return null; // 无法确定位置
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
                // 使用反射直接添加 Comp
                try
                {
                    comp = new CompVirtualIngredient();
                    comp.parent = thingWithComps;
                    comp.Initialize(new CompProperties_VirtualIngredient());
                    
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
                    return null;
                }
            }
            
            if (comp != null)
            {
                comp.SetVirtual(true);
                comp.SetSourceCore(source.core);
                comp.SetSourceMap(source.core.Map);
                
                // 设置预留物品信息
                if (source.isFromReserved && source.reservedThing != null)
                {
                    comp.SetFromReserved(true, source.reservedThing);
                }
                else if (source.reservedThing != null)
                {
                    // 需要补足：标记预留物品和虚拟数量
                    comp.SetFromReserved(false, source.reservedThing);
                }
            }

            return tempThing;
        }

        /// <summary>
        /// 检查 Pawn 是否可以访问虚拟 Thing
        /// </summary>
        private static bool CanPawnAccess(Pawn pawn, Thing thing)
        {
            if (pawn == null || thing == null || !thing.Spawned)
            {
                return false;
            }

            // 检查是否可以保留
            if (!pawn.CanReserve(thing, 1, -1, null, false))
            {
                return false;
            }

            // 检查是否可以到达
            if (!pawn.CanReach(thing, PathEndMode.OnCell, Danger.Deadly))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 为不同的 WorkGiver 创建对应的 Job
        /// </summary>
        private static Job CreateJobForWorkGiver(
            WorkGiver_Scanner workGiver,
            Pawn pawn,
            Thing target,
            Thing item,
            int count)
        {
            Job job = null;

            if (workGiver is WorkGiver_Tend)
            {
                job = JobMaker.MakeJob(JobDefOf.TendPatient, target, item);
                job.count = 1;
            }
            else if (workGiver is WorkGiver_Refuel)
            {
                job = JobMaker.MakeJob(JobDefOf.Refuel, item, target);
                job.count = Math.Min(count, item.stackCount);
            }
            else if (workGiver is WorkGiver_FillFermentingBarrel)
            {
                job = JobMaker.MakeJob(JobDefOf.FillFermentingBarrel, target, item);
                job.count = Math.Min(count, item.stackCount);
            }
            else if (workGiver is WorkGiver_FixBrokenDownBuilding)
            {
                job = JobMaker.MakeJob(JobDefOf.FixBrokenDownBuilding, target, item);
                job.count = 1;
            }

            return job;
        }

        /// <summary>
        /// 获取生成位置（有芯片→InteractionCell，无芯片→输出接口位置）
        /// </summary>
        private static IntVec3 GetSpawnPosition(Pawn pawn, Thing target, Building_StorageCore core, bool hasChip)
        {
            if (hasChip)
            {
                // 有芯片：使用 target 的 InteractionCell（如果存在）
                if (target is Building building && building.def.hasInteractionCell)
                {
                    return building.InteractionCell;
                }
                return pawn.Position;
            }
            else
            {
                // 无芯片：使用输出接口位置
                Building_OutputInterface nearestInterface = FindNearestOutputInterface(pawn.Map, core);
                if (nearestInterface != null && nearestInterface.Spawned)
                {
                    return nearestInterface.Position;
                }
                return IntVec3.Invalid; // 无法访问
            }
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

        /// <summary>
        /// 获取最佳药品（优先级：高级药 > 普通药 > 草药）
        /// </summary>
        private static ThingDef GetBestMedicine(Pawn doctor, Pawn patient)
        {
            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null || patient == null)
            {
                return null;
            }

            ThingDef bestMedicine = null;
            float bestPotency = 0f;

            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered)
                {
                    continue;
                }

                // 先检查预留物品
                SlotGroup slotGroup = core.GetSlotGroup();
                if (slotGroup != null)
                {
                    foreach (Thing thing in slotGroup.HeldThings)
                    {
                        if (thing != null && thing.Spawned && thing.def.IsMedicine)
                        {
                            float potency = thing.def.GetStatValueAbstract(StatDefOf.MedicalPotency);
                            if (potency > bestPotency)
                            {
                                bestPotency = potency;
                                bestMedicine = thing.def;
                            }
                        }
                    }
                }

                // 检查虚拟存储
                foreach (StoredItemData itemData in core.GetAllStoredItems())
                {
                    if (itemData.def == null || !itemData.def.IsMedicine)
                    {
                        continue;
                    }

                    float potency = itemData.def.GetStatValueAbstract(StatDefOf.MedicalPotency);
                    if (potency > bestPotency)
                    {
                        bestPotency = potency;
                        bestMedicine = itemData.def;
                    }
                }
            }

            return bestMedicine;
        }
    }
}

