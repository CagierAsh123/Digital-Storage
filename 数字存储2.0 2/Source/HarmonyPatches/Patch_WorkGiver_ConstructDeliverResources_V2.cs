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
        // 全局同步：记录正在处理的建造目标，防止多个pawn同时创建Job
        private static readonly HashSet<IConstructible> activeConstructionTargets = new HashSet<IConstructible>();

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

            // ⚠️ 全局同步：检查是否已经有其他pawn在处理这个建造目标
            lock (activeConstructionTargets)
            {
                if (activeConstructionTargets.Contains(c))
                {
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Message($"[数字存储] ResourceDeliverJobFor: 其他pawn正在处理建造目标，跳过, pawn={pawn.LabelShort}, target={c.ToString()}");
                    }
                    return;
                }

                // 标记这个建造目标正在被处理
                activeConstructionTargets.Add(c);
            }

            // 获取组件
            Game game = Current.Game;
            DigitalStorageGameComponent gameComp = game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
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

                // 核心原则：绝对优先传送（芯片 > 接口 > 原版）
                // 优先补货：无预留物品时，优先触发补货

                // 1. 查找核心和预留物品状态
                ThingDef foundDef = need.thingDef;
                ThingDef foundStuff = null;
                Building_StorageCore foundCore = null;
                Thing foundReservedThing = null;
                bool hasVirtualStorage = false;
                int reservedCount = 0;

                // 遍历所有核心，查找匹配的材料
                foreach (Building_StorageCore core in gameComp.GetAllCores())
                {
                    if (core == null || !core.Spawned || !core.Powered)
                    {
                        continue;
                    }

                    // 检查预留物品
                    Thing reservedThing = core.FindReservedItem(foundDef, null);
                    if (reservedThing != null && reservedThing.Spawned)
                    {
                        foundReservedThing = reservedThing;
                        foundStuff = reservedThing.Stuff;
                        reservedCount = reservedThing.stackCount;
                    }

                    // 检查虚拟存储
                    foreach (StoredItemData itemData in core.GetAllStoredItems())
                    {
                        if (itemData.def == foundDef)
                        {
                            hasVirtualStorage = itemData.stackCount > 0;
                            if (hasVirtualStorage)
                            {
                                foundCore = core;
                                foundStuff = itemData.stuffDef;  // 更新材质（如果预留没有材质）
                                break;
                            }
                        }
                    }

                    // 如果找到核心（有虚拟存储），停止查找
                    if (foundCore != null)
                    {
                        break;
                    }
                }

                // 如果找不到核心或定义，继续下一个材料
                if (foundCore == null || foundDef == null)
                {
                    continue;
                }

                // 2. 判断三种情况：预留物品状态
                bool reservedSufficient = foundReservedThing != null && reservedCount >= countNeeded;
                bool reservedInsufficient = foundReservedThing != null && reservedCount < countNeeded;
                bool noReserved = foundReservedThing == null;

                // 3. 判断访问权限（芯片/接口）
                bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
                bool hasInterface = HasAccessibleInterface(pawn.Map, gameComp);

                // 4. 根据情况组合处理

                // 情况A：预留物品足够
                if (reservedSufficient)
                {
                    if (hasChip || hasInterface)
                    {
                        // 1-3. 优先传送（即使预留足够）
                        __result = CreateTeleportJob(pawn, c, foundCore, foundDef, foundStuff, countNeeded, null, "预留足够但优先传送");
                    }
                    else
                    {
                        // 4. 无芯片无接口，使用原版逻辑
                        __result = CreateOriginalJob(pawn, c, foundReservedThing, countNeeded, "预留足够使用原版");
                    }
                }
                // 情况B：预留物品不够
                else if (reservedInsufficient)
                {
                    if (hasVirtualStorage)
                    {
                        if (hasChip || hasInterface)
                        {
                            // 5-7. 优先传送（完全从虚拟存储提取）
                            __result = CreateTeleportJob(pawn, c, foundCore, foundDef, foundStuff, countNeeded, foundReservedThing, "预留不够使用传送");
                        }
                        else
                        {
                            // 8. 无访问权限，无Job
                            if (DigitalStorageSettings.enableDebugLog)
                            {
                                Log.Message($"[数字存储] ResourceDeliverJobFor: 预留不够且无访问权限，无Job, 预留={reservedCount}, 需要={countNeeded}");
                            }
                            continue;
                        }
                    }
                    else
                    {
                        // 9-12. 虚拟存储无，无Job
                        if (DigitalStorageSettings.enableDebugLog)
                        {
                            Log.Message($"[数字存储] ResourceDeliverJobFor: 预留不够且虚拟存储无，无Job, 预留={reservedCount}, 需要={countNeeded}");
                        }
                        continue;
                    }
                }
                // 情况C：无预留物品
                else
                {
                    if (hasVirtualStorage)
                    {
                        if (hasChip || hasInterface)
                        {
                            // 13-15. 优先补货，然后传送
                            // ⚠️ 补货前检查是否已经有其他pawn在补货这个物品
                            // 使用ReservationManager检查是否有其他pawn预约了这个物品类型
                            bool canReplenish = true;
                            foreach (Building_StorageCore core in gameComp.GetAllCores())
                            {
                                Thing existingReserved = core.FindReservedItem(foundDef, foundStuff);
                                if (existingReserved != null)
                                {
                                    // 检查是否有其他pawn预约了预留物品
                                    if (!pawn.CanReserve(existingReserved, 1, -1, null, false))
                                    {
                                        canReplenish = false;
                                        if (DigitalStorageSettings.enableDebugLog)
                                        {
                                            Log.Message($"[数字存储] ResourceDeliverJobFor: 其他pawn正在补货，跳过, {existingReserved.Label}");
                                        }
                                        break;
                                    }
                                }
                            }

                            if (!canReplenish)
                            {
                                // 其他pawn正在补货，跳过这个材料
                                continue;
                            }

                            Thing replenishedThing = foundCore.TryReplenishItem(foundDef, foundStuff, DigitalStorageSettings.reservedCountPerItem, QualityCategory.Normal);
                            if (replenishedThing != null)
                            {
                                // 补货成功，重新检查预留物品
                                if (DigitalStorageSettings.enableDebugLog)
                                {
                                    Log.Message($"[数字存储] ResourceDeliverJobFor: 无预留补货成功，重新检查, 补货={replenishedThing.stackCount}");
                                }

                                // 重新查找预留物品（补货后位置可能变化）
                                Thing newReservedThing = foundCore.FindReservedItem(foundDef, foundStuff);
                                if (newReservedThing != null && newReservedThing.stackCount >= countNeeded)
                                {
                                    // 补货后预留足够，按情况A处理
                                    if (hasChip || hasInterface)
                                    {
                                        __result = CreateTeleportJob(pawn, c, foundCore, foundDef, foundStuff, countNeeded, null, "补货后预留足够使用传送");
                                    }
                                    else
                                    {
                                        __result = CreateOriginalJob(pawn, c, newReservedThing, countNeeded, "补货后预留足够使用原版");
                                    }
                                }
                                else
                                {
                                    // 补货后仍不够，按情况B处理
                                    if (hasChip || hasInterface)
                                    {
                                        __result = CreateTeleportJob(pawn, c, foundCore, foundDef, foundStuff, countNeeded, newReservedThing, "补货后仍不够使用传送");
                                    }
                                    else
                                    {
                                        if (DigitalStorageSettings.enableDebugLog)
                                        {
                                            Log.Message($"[数字存储] ResourceDeliverJobFor: 补货后仍不够且无访问权限，无Job, 预留={newReservedThing?.stackCount ?? 0}, 需要={countNeeded}");
                                        }
                                        continue;
                                    }
                                }
                            }
                            else
                            {
                                // 补货失败，继续使用传送（如果有访问权限）
                                __result = CreateTeleportJob(pawn, c, foundCore, foundDef, foundStuff, countNeeded, null, "补货失败使用传送");
                            }
                        }
                        else
                        {
                            // 16. 补货，然后跳转到情况4或12
                            // ⚠️ 补货前检查是否已经有其他pawn在补货这个物品
                            bool canReplenish = true;
                            foreach (Building_StorageCore core in gameComp.GetAllCores())
                            {
                                Thing existingReserved = core.FindReservedItem(foundDef, foundStuff);
                                if (existingReserved != null)
                                {
                                    // 检查是否有其他pawn预约了预留物品
                                    if (!pawn.CanReserve(existingReserved, 1, -1, null, false))
                                    {
                                        canReplenish = false;
                                        if (DigitalStorageSettings.enableDebugLog)
                                        {
                                            Log.Message($"[数字存储] ResourceDeliverJobFor: 其他pawn正在补货，跳过, {existingReserved.Label}");
                                        }
                                        break;
                                    }
                                }
                            }

                            if (!canReplenish)
                            {
                                // 其他pawn正在补货，跳过这个材料
                                continue;
                            }

                            Thing replenishedThing = foundCore.TryReplenishItem(foundDef, foundStuff, DigitalStorageSettings.reservedCountPerItem, QualityCategory.Normal);
                            if (replenishedThing != null)
                            {
                                // 补货成功，跳转到情况4（原版逻辑）
                                Thing newReservedThing = foundCore.FindReservedItem(foundDef, foundStuff);
                                if (newReservedThing != null && newReservedThing.stackCount >= countNeeded)
                                {
                                    __result = CreateOriginalJob(pawn, c, newReservedThing, countNeeded, "补货后预留足够使用原版");
                                }
                                else
                                {
                                    // 补货后仍不够，无Job
                                    if (DigitalStorageSettings.enableDebugLog)
                                    {
                                        Log.Message($"[数字存储] ResourceDeliverJobFor: 补货后仍不够，无Job, 预留={newReservedThing?.stackCount ?? 0}, 需要={countNeeded}");
                                    }
                                    continue;
                                }
                            }
                            else
                            {
                                // 补货失败，无Job
                                if (DigitalStorageSettings.enableDebugLog)
                                {
                                    Log.Message($"[数字存储] ResourceDeliverJobFor: 补货失败且无访问权限，无Job, 需要={countNeeded}");
                                }
                                continue;
                            }
                        }
                    }
                    else
                    {
                        // 17-20. 虚拟存储无，无Job
                        if (DigitalStorageSettings.enableDebugLog)
                        {
                            Log.Message($"[数字存储] ResourceDeliverJobFor: 无预留且虚拟存储无，无Job, 需要={countNeeded}");
                        }
                        continue;
                    }
                }

                // 成功创建Job，返回
                if (DigitalStorageSettings.enableDebugLog)
                {
                    Log.Message($"[数字存储] ResourceDeliverJobFor: 成功创建Job, def={foundDef?.label ?? "null"}");
                }
                return;  // 成功创建Job，结束处理
            }

            // 如果没有创建Job，清理标记
            lock (activeConstructionTargets)
            {
                activeConstructionTargets.Remove(c);
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

        /// <summary>
        /// 清理建造目标标记（供其他类调用）
        /// </summary>
        public static void RemoveActiveConstructionTarget(IConstructible target)
        {
            lock (activeConstructionTargets)
            {
                activeConstructionTargets.Remove(target);
            }
        }

        /// <summary>
        /// 创建传送Job（使用虚拟存储）
        /// </summary>
        private static Job CreateTeleportJob(Pawn pawn, IConstructible c, Building_StorageCore core, ThingDef def, ThingDef stuff, int countNeeded, Thing positionProvider, string reason)
        {
            int countToTake = Math.Min(countNeeded, pawn.carryTracker.MaxStackSpaceEver(def));
            Job job = JobMaker.MakeJob(JobDefOf.HaulToContainer);
            job.targetB = (Thing)c;
            job.count = countToTake;
            job.haulMode = HaulMode.ToContainer;

            // 使用蓝图位置或输出接口位置
            IntVec3 spawnPos = GetSpawnPosition(pawn, core, c, pawn.Map);

            // 创建虚拟 Thing
            Thing tempThing = ThingMaker.MakeThing(def, stuff);
            tempThing.stackCount = countNeeded;
            tempThing.Position = spawnPos;
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
                }
            }

            if (comp != null)
            {
                comp.SetVirtual(true);
                comp.SetSourceCore(core);
                comp.SetSourceMap(core.Map);
                if (positionProvider != null)
                {
                    // 如果有 positionProvider，说明预留物品不够但提供位置
                    // 情况B：预留不够，完全从虚拟存储提取，预留物品仅作为位置提供者
                    comp.SetReservedThingPositionOnly(true, positionProvider);
                }
                else
                {
                    // 没有 positionProvider，说明完全从虚拟存储提取
                    comp.SetFromReserved(false, null);
                }
            }
            else
            {
                // Comp 添加失败，在 targetC 中存储信息
                Thing infoCarrier = ThingMaker.MakeThing(def, stuff);
                infoCarrier.stackCount = countNeeded;
                job.targetC = infoCarrier;
            }

            // 预约虚拟Thing
            Job tempJob = JobMaker.MakeJob(JobDefOf.HaulToContainer);
            tempJob.targetA = tempThing;
            tempJob.targetB = (Thing)c;
            tempJob.count = countToTake;
            tempJob.haulMode = HaulMode.ToContainer;

            if (!pawn.Map.reservationManager.Reserve(pawn, tempJob, tempThing, 1, -1, null, false, false))
            {
                tempThing.Destroy(DestroyMode.Vanish);
                if (DigitalStorageSettings.enableDebugLog)
                {
                    Log.Message($"[数字存储] CreateTeleportJob: 预约失败，销毁虚拟Thing, {reason}");
                }
                return null;
            }

            job.targetA = tempThing;

            // 在 targetC 中存储物品信息
            if (job.targetC.Thing == null)
            {
                Thing infoCarrier = ThingMaker.MakeThing(def, stuff);
                infoCarrier.stackCount = countNeeded;
                job.targetC = infoCarrier;
            }

            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] CreateTeleportJob: 创建传送Job, {def.label} x{countNeeded}, {reason}");
            }

            return job;
        }

        /// <summary>
        /// 创建原版Job（使用预留物品）
        /// </summary>
        private static Job CreateOriginalJob(Pawn pawn, IConstructible c, Thing reservedThing, int countNeeded, string reason)
        {
            // 检查预约
            if (!pawn.CanReserve(reservedThing, 1, -1, null, false))
            {
                if (DigitalStorageSettings.enableDebugLog)
                {
                    Log.Message($"[数字存储] CreateOriginalJob: 预约失败，{reason}, {reservedThing.Label}");
                }
                return null;
            }

            int countToTake = Math.Min(countNeeded, pawn.carryTracker.MaxStackSpaceEver(reservedThing.def));
            Job job = JobMaker.MakeJob(JobDefOf.HaulToContainer);
            job.targetA = reservedThing;
            job.targetB = (Thing)c;
            job.count = countToTake;
            job.haulMode = HaulMode.ToContainer;

            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] CreateOriginalJob: 创建原版Job, {reservedThing.Label} x{countNeeded}, {reason}");
            }

            return job;
        }
    }
}

