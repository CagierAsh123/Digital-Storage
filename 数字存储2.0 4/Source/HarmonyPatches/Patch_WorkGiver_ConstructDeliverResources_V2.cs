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
        // 全局同步：防止同一个建造任务被多次处理
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

            // ⚠️ 全局同步：检查是否已经有其他WorkGiver调用在处理这个建造目标
            lock (activeConstructionTargets)
            {
                if (activeConstructionTargets.Contains(c))
                {
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Message($"[数字存储] ResourceDeliverJobFor: 其他WorkGiver调用正在处理建造目标，明确不提供工作, pawn={pawn.LabelShort}, target={c.ToString()}");
                    }
                    __result = null;  // 明确表示不提供工作，避免CanGiveJob和JobOnX不同步
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

            // ⚠️ 重新设计逻辑：总是接管建造材料运送，为每种材料创建我们自己的job
            // 这样可以确保虚拟存储逻辑被正确应用，避免job重复创建

            // 为每种材料创建job
            foreach (ThingDefCountClass need in c.TotalMaterialCost())
            {
                int countNeeded = GetCountNeeded(c, need.thingDef, pawn, forced);
                if (countNeeded <= 0)
                {
                    continue;
                }

                // 查找核心和预留物品状态
                ThingDef foundDef = need.thingDef;
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
                        reservedCount = reservedThing.stackCount;
                        foundCore = core;
                    }

                    // 检查虚拟存储
                    foreach (StoredItemData itemData in core.GetAllStoredItems())
                    {
                        if (itemData.def == foundDef)
                        {
                            hasVirtualStorage = itemData.stackCount > 0;
                            if (hasVirtualStorage && foundCore == null)
                            {
                                foundCore = core;
                            }
                            break;
                        }
                    }

                    // 如果找到核心（有虚拟存储或预留），停止查找
                    if (foundCore != null)
                    {
                        break;
                    }
                }

                // 如果找不到核心，继续下一个材料（让原版处理）
                if (foundCore == null)
                {
                    continue;
                }

                // 创建我们自己的job
                if (DigitalStorageSettings.enableDebugLog)
                {
                    Log.Message($"[数字存储] ResourceDeliverJobFor: 创建材料运送Job, def={foundDef.label}, count={countNeeded}, pawn={pawn.LabelShort}, target={c.ToString()}");
                }

                __result = CreateTeleportJob(pawn, c, foundCore, foundDef, null, countNeeded, foundReservedThing, "为每种材料创建job");
                return;  // 只为第一个材料创建job，避免同时创建多个
            }

            // 如果没有创建Job，清理标记并明确表示不提供工作
            lock (activeConstructionTargets)
            {
                activeConstructionTargets.Remove(c);
            }

            // 明确表示不提供工作，避免CanGiveJob和JobOnX不同步
            __result = null;
            return;
        }

        public static void RemoveActiveConstructionTarget(IConstructible target)
        {
            lock (activeConstructionTargets)
            {
                activeConstructionTargets.Remove(target);
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
                // 有芯片：传送到蓝图位置
                if (c is Thing thing)
                {
                    return thing.Position;
                }
                return pawn.Position;
            }
            else
            {
                // 无芯片：传送到输出接口位置
                Building_OutputInterface outputInterface = FindNearestOutputInterface(map, core);
                if (outputInterface != null)
                {
                    return outputInterface.Position;
                }
                return pawn.Position;
            }
        }

        private static int GetCountNeeded(IConstructible c, ThingDef def, Pawn pawn, bool forced)
        {
            return c.ThingCountNeeded(def);
        }

        private static Building_OutputInterface FindNearestOutputInterface(Map map, Building_StorageCore core)
        {
            if (map == null)
            {
                return null;
            }

            Building_OutputInterface nearest = null;
            float nearestDist = float.MaxValue;

            foreach (Building_OutputInterface outputInterface in map.listerBuildings.AllBuildingsColonistOfClass<Building_OutputInterface>())
            {
                if (outputInterface.BoundCore == core)
                {
                    float dist = outputInterface.Position.DistanceTo(core.Position);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearest = outputInterface;
                    }
                }
            }

            return nearest;
        }

        private static bool HasAccessibleInterface(Map map, DigitalStorageGameComponent gameComp)
        {
            if (map == null || gameComp == null)
            {
                return false;
            }

            foreach (Building_OutputInterface outputInterface in map.listerBuildings.AllBuildingsColonistOfClass<Building_OutputInterface>())
            {
                if (outputInterface.BoundCore != null && outputInterface.BoundCore.Powered)
                {
                    return true;
                }
            }

            return false;
        }

        private static Job CreateTeleportJob(Pawn pawn, IConstructible c, Building_StorageCore core, ThingDef def, ThingDef stuff, int countNeeded, Thing positionProvider, string reason)
        {
            int countToTake = Math.Min(countNeeded, pawn.carryTracker.MaxStackSpaceEver(def));
            Job job = JobMaker.MakeJob(JobDefOf.HaulToContainer);
            job.targetB = (Thing)c;
            job.count = countToTake;

            // 创建虚拟Thing作为job.targetA
            Thing virtualThing = ThingMaker.MakeThing(def, stuff);
            virtualThing.stackCount = countToTake;

            // 添加CompVirtualIngredient标记（使用反射）
            if (virtualThing is ThingWithComps thingWithComps)
            {
                try
                {
                    CompVirtualIngredient comp = new CompVirtualIngredient();
                    comp.parent = thingWithComps;
                    comp.Initialize(new CompProperties_VirtualIngredient());

                    var compsField = typeof(ThingWithComps).GetField("comps", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
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

                    // 设置虚拟标记
                    comp.SetVirtual(true);
                    comp.SetSourceCore(core);
                    comp.SetSourceMap(core.Map);
                    if (positionProvider != null)
                    {
                        comp.SetReservedThingPositionOnly(true, positionProvider);
                    }
                    else
                    {
                        comp.SetFromReserved(false, null);
                    }
                }
                catch (Exception ex)
                {
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Warning($"[数字存储] 无法添加CompVirtualIngredient到虚拟Thing: {ex.Message}");
                    }
                }
            }

            job.targetA = virtualThing;

            // 清除任何现有的预留
            pawn.ClearReservationsForJob(job);

            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] CreateTeleportJob: {reason}, def={def.label}, count={countToTake}, pawn={pawn.LabelShort}");
            }

            return job;
        }

        private static Job CreateOriginalJob(Pawn pawn, IConstructible c, Thing reservedThing, int countNeeded, string reason)
        {
            if (reservedThing == null)
            {
                return null;
            }

            Job job = JobMaker.MakeJob(JobDefOf.HaulToContainer);
            job.targetA = reservedThing;
            job.targetB = (Thing)c;
            job.count = Math.Min(countNeeded, reservedThing.stackCount);

            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] CreateOriginalJob: {reason}, def={reservedThing.def.label}, count={job.count}, pawn={pawn.LabelShort}");
            }

            return job;
        }
    }
}