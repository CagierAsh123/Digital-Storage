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

                foreach (Building_StorageCore core in gameComp.GetAllCores())
                {
                    if (core == null || !core.Spawned || !core.Powered)
                    {
                        continue;
                    }

                    // 检查预留物品
                    Thing reservedThing = core.FindReservedItem(need.thingDef, null);
                    if (reservedThing != null && reservedThing.Spawned && reservedThing.stackCount >= countNeeded)
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

                // 如果找到核心，为这个材料创建job
                if (materialCore != null)
                {
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Message($"[数字存储] ResourceDeliverJobFor: 为材料{need.thingDef.label}创建传送Job, pawn={pawn.LabelShort}, target={c.ToString()}");
                    }

                    __result = CreateTeleportJob(pawn, c, materialCore, need.thingDef, null, countNeeded, null, "为单个材料创建job");
                    return;
                }
            }

            // 如果没有找到任何可处理的材料，让原版逻辑处理
            return;
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
            return c.ThingCountNeeded(def);
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
        /// 创建传送Job（使用虚拟存储）
        /// </summary>
        private static Job CreateTeleportJob(Pawn pawn, IConstructible c, Building_StorageCore core, ThingDef def, ThingDef stuff, int countNeeded, Thing positionProvider, string reason)
        {
            int countToTake = Math.Min(countNeeded, pawn.carryTracker.MaxStackSpaceEver(def));
            Job job = JobMaker.MakeJob(JobDefOf.HaulToContainer);
            job.targetB = (Thing)c;
            job.count = countToTake;

            // 创建虚拟Thing
            Thing tempThing = ThingMaker.MakeThing(def, stuff);
            tempThing.stackCount = countNeeded;
            IntVec3 spawnPos = GetSpawnPosition(pawn, core, c, pawn.Map);
            GenSpawn.Spawn(tempThing, spawnPos, pawn.Map, WipeMode.Vanish);

            // 添加Comp标记
            if (tempThing is ThingWithComps thingWithComps)
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
                        Log.Warning($"[数字存储] 无法添加CompVirtualIngredient: {ex.Message}");
                    }
                }
            }

            // 预约虚拟Thing
            if (!pawn.Map.reservationManager.Reserve(pawn, job, tempThing, 1, -1, null, false, false))
            {
                tempThing.Destroy(DestroyMode.Vanish);
                return null;
            }

            job.targetA = tempThing;
            Thing infoThing = ThingMaker.MakeThing(def, stuff);
            infoThing.stackCount = countNeeded;
            job.targetC = infoThing;

            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] CreateTeleportJob: {reason}, def={def.label}, count={countNeeded}");
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