// Job启动时的传送逻辑
// DoBill/HaulToContainer: 有芯片时传送材料到工作位置

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
            Thing billGiver = job.targetA.Thing;
            if (billGiver == null || billGiver.Map == null)
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

            // 获取工作台位置
            IntVec3 workbenchPos = GetBillGiverRootCell(billGiver, pawn);
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

                int countNeeded = job.countQueue[i];

                // 检查材料是否在核心的SlotGroup中
                Building_StorageCore materialCore = FindCoreWithItem(material, pawn.Map);
                if (materialCore != null)
                {
                    hasVirtualMaterial = true;
                    
                    Thing thingToTeleport;
                    if (material.stackCount <= countNeeded)
                    {
                        // 整个物品传送
                        material.DeSpawn(0);
                        GenSpawn.Spawn(material, workbenchPos, pawn.Map, WipeMode.Vanish);
                        thingToTeleport = material;
                    }
                    else
                    {
                        // 分离需要的数量后传送
                        thingToTeleport = material.SplitOff(countNeeded);
                        GenSpawn.Spawn(thingToTeleport, workbenchPos, pawn.Map, WipeMode.Vanish);
                    }
                    
                    FleckMaker.ThrowLightningGlow(thingToTeleport.DrawPos, thingToTeleport.Map, 0.5f);
                    job.targetQueueB[i] = thingToTeleport;
                    
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Message($"[数字存储] DoBill传送材料: {thingToTeleport.Label} x{thingToTeleport.stackCount} 到工作台 {workbenchPos}");
                    }
                }
            }

            if (hasVirtualMaterial)
            {
                // 保留原有预约，避免其它 pawn 抢走已分配材料
            }
        }
        
        private static IntVec3 GetBillGiverRootCell(Thing billGiver, Pawn pawn)
        {
            Building building = billGiver as Building;
            if (building == null)
            {
                return billGiver.Position;
            }
            if (building.def.hasInteractionCell)
            {
                return building.InteractionCell;
            }
            return pawn.Position;
        }

        private static void HandleHaulToContainer(Job job, Pawn pawn)
        {
            if (DigitalStorageSettings.enableDebugLog)
                Log.Message($"[数字存储] HandleHaulToContainer: pawn={pawn.Name}, job.targetA={job.targetA}, job.count={job.count}");
            
            if (job.targetA.Thing == null || job.targetA.Thing.Map == null)
            {
                return;
            }

            bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
            if (!hasChip)
            {
                return; // 无芯片不传送
            }

            Thing material = job.targetA.Thing;
            if (material == null || material.Destroyed)
            {
                return;
            }

            // 检查材料是否在核心的SlotGroup中
            Building_StorageCore materialCore = FindCoreWithItem(material, pawn.Map);
            
            if (materialCore != null)
            {
                // 确定需要传送的数量
                int countNeeded = job.count > 0 ? job.count : material.stackCount;
                
                Thing thingToTeleport;
                if (material.stackCount > countNeeded)
                {
                    // 物品多于需要，先分出需要的数量
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
                
                // 保留原有预约（ReservationManager 追踪的是 Thing 引用而不是位置）
                
                if (DigitalStorageSettings.enableDebugLog)
                    Log.Message($"[数字存储] HaulToContainer 传送: {thingToTeleport.Label} x{thingToTeleport.stackCount} 到 {pawn.Position}");
            }
        }

        private static Building_StorageCore FindCoreWithItem(Thing item, Map map)
        {
            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return null;
            }

            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered || core.Map != map)
                {
                    continue;
                }

                SlotGroup slotGroup = core.GetSlotGroup();
                if (slotGroup != null)
                {
                    // 方法1：检查 HeldThings
                    foreach (Thing thing in slotGroup.HeldThings)
                    {
                        if (thing == item)
                        {
                            return core;
                        }
                    }
                    
                    // 方法2：检查物品位置是否在 SlotGroup 的格子范围内
                    if (slotGroup.CellsList.Contains(item.Position))
                    {
                        return core;
                    }
                }
            }

            return null;
        }
    }
}
