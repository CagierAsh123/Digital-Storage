// Job启动时的传送逻辑
// 有芯片：传送到pawn脚底
// 无芯片：不传送，让原版处理

using System;
using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Components;
using DigitalStorage.Data;
using DigitalStorage.Services;
using DigitalStorage.Settings;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    [HarmonyPatch(typeof(Pawn_JobTracker), "StartJob")]
    public static class Patch_Pawn_JobTracker_StartJob_Teleport
    {
        public static Dictionary<Job, List<Thing>> jobExtractedItems = new Dictionary<Job, List<Thing>>();

        public static bool Prefix(Job newJob, Pawn ___pawn)
        {
            if (newJob == null || ___pawn == null || ___pawn.Map == null || newJob.def == null)
            {
                return true;
            }

            // 处理 DoBill（工作台制作）
            if (newJob.def == JobDefOf.DoBill && newJob.countQueue != null && newJob.targetQueueB != null)
            {
                HandleDoBill(newJob, ___pawn);
            }
            // 处理 HaulToContainer（蓝图建造）
            else if (newJob.def == JobDefOf.HaulToContainer)
            {
                HandleHaulToContainer(newJob, ___pawn);
            }

            return true;
        }

        private static void HandleDoBill(Job job, Pawn pawn)
        {
            if (job.targetA.Thing == null || job.targetA.Thing.Map == null)
            {
                return;
            }

            if (job.targetQueueB == null || job.countQueue == null)
            {
                return;
            }

            bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
            if (!hasChip)
            {
                return; // 无芯片不传送
            }

            bool hasVirtualMaterial = false;

            for (int i = 0; i < job.targetQueueB.Count; i++)
            {
                if (i >= job.countQueue.Count)
                {
                    continue;
                }

                LocalTargetInfo targetInfo = job.targetQueueB[i];
                if (!targetInfo.IsValid || !targetInfo.HasThing)
                {
                    continue;
                }

                Thing material = targetInfo.Thing;
                if (material == null || material.Destroyed)
                {
                    continue;
                }

                // 检查材料是否在核心的SlotGroup中
                Building_StorageCore materialCore = FindCoreWithItem(material, pawn.Map);
                if (materialCore != null)
                {
                    hasVirtualMaterial = true;
                    
                    // 传送到pawn脚底
                    material.DeSpawn(0);
                    GenSpawn.Spawn(material, pawn.Position, pawn.Map, WipeMode.Vanish);
                    FleckMaker.ThrowLightningGlow(material.DrawPos, material.Map, 0.5f);
                    
                    job.targetQueueB[i] = material;
                    
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Message($"[数字存储] 传送材料: {material.Label} x{material.stackCount} 到 {pawn.Position}");
                    }
                }
            }

            if (hasVirtualMaterial)
            {
                pawn.ClearReservationsForJob(job);
            }
        }

        private static void HandleHaulToContainer(Job job, Pawn pawn)
        {
            Log.Message($"[数字存储] HandleHaulToContainer: 开始处理, pawn={pawn.Name}, job.targetA={job.targetA}, job.count={job.count}");
            
            if (job.targetA.Thing == null || job.targetA.Thing.Map == null)
            {
                Log.Message($"[数字存储] HandleHaulToContainer: targetA.Thing 为空或 Map 为空");
                return;
            }

            bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
            Log.Message($"[数字存储] HandleHaulToContainer: hasChip={hasChip}");
            if (!hasChip)
            {
                return; // 无芯片不传送
            }

            Thing material = job.targetA.Thing;
            if (material == null || material.Destroyed)
            {
                Log.Message($"[数字存储] HandleHaulToContainer: material 为空或已销毁");
                return;
            }

            Log.Message($"[数字存储] HandleHaulToContainer: material={material.Label} x{material.stackCount} at {material.Position}");

            // 检查材料是否在核心的SlotGroup中
            Building_StorageCore materialCore = FindCoreWithItem(material, pawn.Map);
            Log.Message($"[数字存储] HandleHaulToContainer: FindCoreWithItem 返回 {(materialCore != null ? materialCore.NetworkName : "null")}");
            
            if (materialCore != null)
            {
                // 确定需要传送的数量
                int countNeeded = job.count > 0 ? job.count : material.stackCount;
                
                Thing thingToTeleport;
                if (material.stackCount > countNeeded)
                {
                    // 物品多于需要，先分出需要的数量
                    Log.Message($"[数字存储] HandleHaulToContainer: 物品 {material.stackCount} > 需要 {countNeeded}，分离");
                    thingToTeleport = material.SplitOff(countNeeded);
                    GenSpawn.Spawn(thingToTeleport, material.Position, pawn.Map, WipeMode.Vanish);
                }
                else
                {
                    // 物品等于或少于需要，传送整个物品
                    thingToTeleport = material;
                }
                
                // 传送到pawn脚底
                thingToTeleport.DeSpawn(0);
                GenSpawn.Spawn(thingToTeleport, pawn.Position, pawn.Map, WipeMode.Vanish);
                FleckMaker.ThrowLightningGlow(thingToTeleport.DrawPos, thingToTeleport.Map, 0.5f);
                
                // 更新Job的target
                job.targetA = thingToTeleport;
                
                // 清理预留（因为位置变了）
                pawn.ClearReservationsForJob(job);
                
                Log.Message($"[数字存储] HaulToContainer 传送材料: {thingToTeleport.Label} x{thingToTeleport.stackCount} 到 {pawn.Position}");
            }
        }

        private static Building_StorageCore FindCoreWithItem(Thing item, Map map)
        {
            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                Log.Message($"[数字存储] FindCoreWithItem: gameComp 为空");
                return null;
            }

            // 改用 GetAllCores() 并手动过滤，避免 coresByMap 缓存问题
            List<Building_StorageCore> allCores = gameComp.GetAllCores();
            Log.Message($"[数字存储] FindCoreWithItem: GetAllCores 返回 {allCores.Count} 个核心, 物品位置={item.Position}, 目标地图={map}");
            
            foreach (Building_StorageCore core in allCores)
            {
                if (core == null || !core.Spawned || !core.Powered)
                {
                    continue;
                }

                // 手动过滤当前地图的核心
                if (core.Map != map)
                {
                    Log.Message($"[数字存储] FindCoreWithItem: 核心={core.NetworkName} 在地图={core.Map}，跳过（不是目标地图）");
                    continue;
                }

                Log.Message($"[数字存储] FindCoreWithItem: 检查核心={core.NetworkName}, 核心位置={core.Position}");

                SlotGroup slotGroup = core.GetSlotGroup();
                if (slotGroup != null)
                {
                    // 方法1：检查 HeldThings
                    foreach (Thing thing in slotGroup.HeldThings)
                    {
                        if (thing == item)
                        {
                            Log.Message($"[数字存储] FindCoreWithItem: 方法1匹配成功（HeldThings）");
                            return core;
                        }
                    }
                    
                    // 方法2：检查物品位置是否在 SlotGroup 的格子范围内
                    if (slotGroup.CellsList.Contains(item.Position))
                    {
                        Log.Message($"[数字存储] FindCoreWithItem: 方法2匹配成功（位置在SlotGroup范围内）");
                        return core;
                    }
                }
            }

            Log.Message($"[数字存储] FindCoreWithItem: 没有找到匹配的核心");
            return null;
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker), "CleanupCurrentJob")]
    public static class Patch_Pawn_JobTracker_CleanupCurrentJob
    {
        public static void Postfix(JobCondition condition, Pawn ___pawn, Job ___curJob)
        {
            if (___curJob == null)
            {
                return;
            }

            lock (Patch_Pawn_JobTracker_StartJob_Teleport.jobExtractedItems)
            {
                if (Patch_Pawn_JobTracker_StartJob_Teleport.jobExtractedItems.ContainsKey(___curJob))
                {
                    Patch_Pawn_JobTracker_StartJob_Teleport.jobExtractedItems.Remove(___curJob);
                }
            }
        }
    }
}
