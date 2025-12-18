// TODO: 待重写 - 新架构
// 重构为虚拟 Thing 检测和传送机制
// 核心逻辑：
// 1. 检测 Job 中的虚拟材料标记（CompVirtualIngredient）
// 2. 从虚拟存储提取真实物品（跨地图）
// 3. 传送到 Pawn 位置（有芯片）或输出接口（无芯片）
// 4. 替换 Job 的 targetQueueB 为真实物品
// 5. 销毁临时虚拟 Thing

using System;
using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Components;
using DigitalStorage.Services;
using DigitalStorage.Settings;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 简化的 Job Hook：参考物品传送仪，但支持所有 Job 类型
    /// 核心逻辑：检测物品在核心中时，从虚拟存储传送并替换目标
    /// 不需要 GhostMarker：预留物品系统确保 WorkGiver 能找到真实物品
    /// </summary>
    [HarmonyPatch(typeof(Pawn_JobTracker), "StartJob")]
    public static class Patch_Pawn_JobTracker_StartJob_Teleport
    {
        public static void Prefix(Job newJob, Pawn ___pawn)
        {
            if (newJob == null || ___pawn == null || ___pawn.Map == null)
            {
                return;
            }

            // 确保 Job.def 不为 null
            if (newJob.def == null)
            {
                return;
            }

            // 处理 DoBill（工作台制作）- 类似物品传送仪
            if (newJob.def == JobDefOf.DoBill && newJob.countQueue != null && newJob.targetQueueB != null)
            {
                HandleDoBill(newJob, ___pawn);
            }
            // 处理 HaulToCell（搬运）- 类似物品传送仪
            else if (newJob.def == JobDefOf.HaulToCell)
            {
                HandleHaulToCell(newJob, ___pawn);
            }
            // 处理 HaulToContainer（蓝图建造）- 新架构：检测虚拟材料并传送
            else if (newJob.def == JobDefOf.HaulToContainer && newJob.targetA.HasThing && newJob.targetB.HasThing)
            {
                HandleHaulToContainer(newJob, ___pawn);
            }
            // 处理 Ingest（吃饭）- Pro Max 独有
            else if (newJob.def == JobDefOf.Ingest)
            {
                HandleIngest(newJob, ___pawn);
            }
            // 处理 TendPatient（医疗）- Pro Max 独有
            else if (newJob.def == JobDefOf.TendPatient)
            {
                HandleTendPatient(newJob, ___pawn);
            }
            // 处理 Refuel（加燃料）- Pro Max 独有
            else if (newJob.def == JobDefOf.Refuel)
            {
                HandleRefuel(newJob, ___pawn);
            }
            // 处理其他需要物品的 Job（检查 targetA 或 targetB）
            else if ((newJob.targetA.HasThing && newJob.targetA.Thing != null) || 
                     (newJob.targetB.HasThing && newJob.targetB.Thing != null))
            {
                HandleGenericJob(newJob, ___pawn);
            }
        }

        /// <summary>
        /// 处理 DoBill（工作台制作）- 新架构：检测虚拟材料并传送
        /// </summary>
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

            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            bool hasVirtualMaterial = false;

            // 处理每个材料
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

                // 检查是否是虚拟材料
                CompVirtualIngredient virtualComp = null;
                if (material is ThingWithComps materialWithComps)
                {
                    virtualComp = materialWithComps.GetComp<CompVirtualIngredient>();
                }
                if (virtualComp == null || !virtualComp.IsVirtual)
                {
                    // 不是虚拟材料，跳过（可能是预留物品或其他来源）
                    continue;
                }

                hasVirtualMaterial = true;

                // 获取虚拟材料的来源信息
                Building_StorageCore sourceCore = virtualComp.SourceCore;
                Map sourceMap = virtualComp.SourceMap ?? pawn.Map;
                int neededCount = job.countQueue[i];
                Thing realThing = null;

                // 检查是否来自预留物品
                if (virtualComp.IsFromReserved && virtualComp.ReservedThing != null)
                {
                    Thing reservedThing = virtualComp.ReservedThing;
                    
                    // 验证预留物品仍然有效
                    if (reservedThing.Spawned && reservedThing.stackCount > 0 && 
                        reservedThing.def == material.def && reservedThing.Stuff == material.Stuff)
                    {
                        // 从预留物品 SplitOff 需要的数量
                        if (reservedThing.stackCount >= neededCount)
                        {
                            realThing = reservedThing.SplitOff(neededCount);
                        }
                        else
                        {
                            // 预留物品不够，使用全部预留物品，剩余从虚拟存储补足
                            realThing = reservedThing.SplitOff(reservedThing.stackCount);
                            int remaining = neededCount - realThing.stackCount;
                            
                            // 从虚拟存储提取剩余部分
                            Thing virtualPart = null;
                            if (sourceCore != null && sourceCore.Spawned && sourceCore.Powered)
                            {
                                virtualPart = sourceCore.ExtractItem(material.def, remaining, material.Stuff);
                            }
                            else
                            {
                                virtualPart = gameComp.TryExtractItemFromAnyCoreGlobal(
                                    material.def,
                                    remaining,
                                    material.Stuff,
                                    pawn.Map
                                );
                            }
                            
                            if (virtualPart != null)
                            {
                                // 尝试合并到预留物品堆叠
                                if (realThing.TryAbsorbStack(virtualPart, true))
                                {
                                    // 合并成功
                                }
                                else
                                {
                                    // 无法合并，保留两个堆叠
                                    // 这里简化处理：如果无法合并，使用虚拟部分
                                    realThing.Destroy(DestroyMode.Vanish);
                                    realThing = virtualPart;
                                }
                            }
                        }
                    }
                }
                else
                {
                    // 完全来自虚拟存储，或需要补足但预留物品不可用
                    if (virtualComp.ReservedThing != null)
                    {
                        // 需要补足：先尝试从预留物品获取可用数量
                        Thing reservedThing = virtualComp.ReservedThing;
                        if (reservedThing.Spawned && reservedThing.stackCount > 0 &&
                            reservedThing.def == material.def && reservedThing.Stuff == material.Stuff)
                        {
                            int reservedAvailable = reservedThing.stackCount;
                            if (reservedAvailable > 0)
                            {
                                realThing = reservedThing.SplitOff(reservedAvailable);
                                int remaining = neededCount - realThing.stackCount;
                                
                                // 从虚拟存储提取剩余部分
                                Thing virtualPart = null;
                                if (sourceCore != null && sourceCore.Spawned && sourceCore.Powered)
                                {
                                    virtualPart = sourceCore.ExtractItem(material.def, remaining, material.Stuff);
                                }
                                else
                                {
                                    virtualPart = gameComp.TryExtractItemFromAnyCoreGlobal(
                                        material.def,
                                        remaining,
                                        material.Stuff,
                                        pawn.Map
                                    );
                                }
                                
                                if (virtualPart != null)
                                {
                                    // 尝试合并
                                    if (!realThing.TryAbsorbStack(virtualPart, true))
                                    {
                                        // 无法合并，使用虚拟部分
                                        realThing.Destroy(DestroyMode.Vanish);
                                        realThing = virtualPart;
                                    }
                                }
                            }
                        }
                    }
                    
                    // 如果还没有获取到物品，完全从虚拟存储提取
                    if (realThing == null)
                    {
                        if (sourceCore != null && sourceCore.Spawned && sourceCore.Powered)
                        {
                            realThing = sourceCore.ExtractItem(material.def, neededCount, material.Stuff);
                        }
                        else
                        {
                            realThing = gameComp.TryExtractItemFromAnyCoreGlobal(
                                material.def,
                                neededCount,
                                material.Stuff,
                                pawn.Map
                            );
                        }
                    }
                }

                if (realThing == null)
                {
                    // 提取失败，跳过这个材料（Job 可能会失败）
                    continue;
                }

                // 销毁临时虚拟 Thing
                if (material.Spawned)
                {
                    material.DeSpawn(DestroyMode.Vanish);
                }
                material.Destroy(DestroyMode.Vanish);

                // 传送到 Pawn 位置（有芯片）或输出接口（无芯片）
                IntVec3 spawnPos = GetSpawnPosition(pawn, sourceCore ?? gameComp.FindCoreWithItemType(material.def, material.Stuff, pawn.Map), pawn.Map);
                GenSpawn.Spawn(realThing, spawnPos, pawn.Map, WipeMode.Vanish);
                FleckMaker.ThrowLightningGlow(realThing.DrawPos, realThing.Map, 0.5f);

                // 替换 Job 目标
                job.targetQueueB[i] = realThing;

                if (DigitalStorageSettings.enableDebugLog)
                {
                    string mapName = "未知地图";
                    if (sourceMap != null)
                    {
                        // 使用 Map.Index 作为地图标识
                        mapName = $"地图 {sourceMap.Index}";
                    }
                    string sourceType = virtualComp.IsFromReserved ? "预留物品" : "虚拟存储";
                    Log.Message($"[数字存储] 传送材料: {realThing.Label} x{realThing.stackCount} 从 {sourceType} ({mapName}) 到 {pawn.Position}");
                }
            }

            // 如果有虚拟材料被替换，清除 Pawn 的保留（因为目标已改变）
            if (hasVirtualMaterial)
            {
                pawn.ClearReservationsForJob(job);
            }
        }

        /// <summary>
        /// 处理 HaulToCell（搬运）- 参考物品传送仪
        /// </summary>
        private static void HandleHaulToCell(Job job, Pawn pawn)
        {
            if (!job.targetA.HasThing || job.targetA.Thing == null)
            {
                return;
            }

            Thing thing = job.targetA.Thing;
            if (thing.Map == null)
            {
                return;
            }

            // 检查是否在核心中
            Building_StorageCore core = FindCoreWithItem(thing, pawn.Map);
            if (core == null)
            {
                return;
            }

            // 检查目标位置
            if (!job.targetB.IsValid)
            {
                return;
            }

            IntVec3 targetCell = job.targetB.Cell;
            SlotGroup targetSlotGroup = StoreUtility.GetSlotGroup(targetCell, pawn.Map);
            SlotGroup sourceSlotGroup = StoreUtility.GetSlotGroup(thing.Position, pawn.Map);

            // 如果源和目标在同一个存储组，不需要传送
            if (sourceSlotGroup != null && targetSlotGroup != null && 
                sourceSlotGroup.parent == targetSlotGroup.parent)
            {
                return;
            }

            // 检查物品位置是否已经有传送仪（避免重复传送）
            List<Thing> thingsAtPos = thing.Position.GetThingList(thing.Map);
            if (thingsAtPos.Any(t => t is Building_StorageCore || t is Building_OutputInterface))
            {
                return;
            }

            // 从虚拟存储提取并传送
            int neededCount = job.count > 0 ? job.count : thing.stackCount;
            Thing extracted = null;

            if (thing.stackCount <= neededCount)
            {
                // 整个物品都需要
                extracted = core.ExtractItem(thing.def, neededCount, thing.Stuff);
                if (extracted != null)
                {
                    thing.DeSpawn(DestroyMode.Vanish);
                    GenSpawn.Spawn(extracted, GetSpawnPosition(pawn, core, thing.Map), thing.Map);
                    FleckMaker.ThrowLightningGlow(extracted.DrawPos, extracted.Map, 0.5f);
                    job.targetA = extracted;
                    pawn.ClearReservationsForJob(job);
                }
            }
            else
            {
                // 只需要部分，分割
                Thing split = thing.SplitOff(neededCount);
                if (split != null)
                {
                    GenSpawn.Spawn(split, GetSpawnPosition(pawn, core, thing.Map), thing.Map);
                    FleckMaker.ThrowLightningGlow(split.DrawPos, split.Map, 0.5f);
                    job.targetA = split;
                    pawn.ClearReservationsForJob(job);
                }
            }
        }

        /// <summary>
        /// 处理 HaulToContainer（蓝图建造）- 新架构：检测虚拟材料并传送
        /// </summary>
        private static void HandleHaulToContainer(Job job, Pawn pawn)
        {
            if (!job.targetA.HasThing || job.targetA.Thing == null)
            {
                return;
            }

            Thing material = job.targetA.Thing;
            if (material.Map == null)
            {
                return;
            }

            // 检查是否是虚拟材料
            CompVirtualIngredient virtualComp = null;
            if (material is ThingWithComps materialWithComps)
            {
                virtualComp = materialWithComps.GetComp<CompVirtualIngredient>();
            }
            if (virtualComp == null || !virtualComp.IsVirtual)
            {
                // 不是虚拟材料，跳过（可能是预留物品或其他来源）
                return;
            }

            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            // 获取虚拟材料的来源信息
            Building_StorageCore sourceCore = virtualComp.SourceCore;
            Map sourceMap = virtualComp.SourceMap ?? pawn.Map;
            int neededCount = job.count > 0 ? job.count : material.stackCount;
            Thing realThing = null;

            // 检查是否来自预留物品
            if (virtualComp.IsFromReserved && virtualComp.ReservedThing != null)
            {
                Thing reservedThing = virtualComp.ReservedThing;
                
                // 验证预留物品仍然有效
                if (reservedThing.Spawned && reservedThing.stackCount > 0 && 
                    reservedThing.def == material.def && reservedThing.Stuff == material.Stuff)
                {
                    // 从预留物品 SplitOff 需要的数量
                    if (reservedThing.stackCount >= neededCount)
                    {
                        realThing = reservedThing.SplitOff(neededCount);
                    }
                    else
                    {
                        // 预留物品不够，使用全部预留物品，剩余从虚拟存储补足
                        realThing = reservedThing.SplitOff(reservedThing.stackCount);
                        int remaining = neededCount - realThing.stackCount;
                        
                        // 从虚拟存储提取剩余部分
                        Thing virtualPart = null;
                        if (sourceCore != null && sourceCore.Spawned && sourceCore.Powered)
                        {
                            virtualPart = sourceCore.ExtractItem(material.def, remaining, material.Stuff);
                        }
                        else
                        {
                            virtualPart = gameComp.TryExtractItemFromAnyCoreGlobal(
                                material.def,
                                remaining,
                                material.Stuff,
                                pawn.Map
                            );
                        }
                        
                        if (virtualPart != null)
                        {
                            // 尝试合并到预留物品堆叠
                            if (!realThing.TryAbsorbStack(virtualPart, true))
                            {
                                // 无法合并，使用虚拟部分
                                realThing.Destroy(DestroyMode.Vanish);
                                realThing = virtualPart;
                            }
                        }
                    }
                }
            }
            else
            {
                // 完全来自虚拟存储，或需要补足但预留物品不可用
                if (virtualComp.ReservedThing != null)
                {
                    // 需要补足：先尝试从预留物品获取可用数量
                    Thing reservedThing = virtualComp.ReservedThing;
                    if (reservedThing.Spawned && reservedThing.stackCount > 0 &&
                        reservedThing.def == material.def && reservedThing.Stuff == material.Stuff)
                    {
                        int reservedAvailable = reservedThing.stackCount;
                        if (reservedAvailable > 0)
                        {
                            realThing = reservedThing.SplitOff(reservedAvailable);
                            int remaining = neededCount - realThing.stackCount;
                            
                            // 从虚拟存储提取剩余部分
                            Thing virtualPart = null;
                            if (sourceCore != null && sourceCore.Spawned && sourceCore.Powered)
                            {
                                virtualPart = sourceCore.ExtractItem(material.def, remaining, material.Stuff);
                            }
                            else
                            {
                                virtualPart = gameComp.TryExtractItemFromAnyCoreGlobal(
                                    material.def,
                                    remaining,
                                    material.Stuff,
                                    pawn.Map
                                );
                            }
                            
                            if (virtualPart != null)
                            {
                                // 尝试合并
                                if (!realThing.TryAbsorbStack(virtualPart, true))
                                {
                                    // 无法合并，使用虚拟部分
                                    realThing.Destroy(DestroyMode.Vanish);
                                    realThing = virtualPart;
                                }
                            }
                        }
                    }
                }
                
                // 如果还没有获取到物品，完全从虚拟存储提取
                if (realThing == null)
                {
                    if (sourceCore != null && sourceCore.Spawned && sourceCore.Powered)
                    {
                        realThing = sourceCore.ExtractItem(material.def, neededCount, material.Stuff);
                    }
                    else
                    {
                        realThing = gameComp.TryExtractItemFromAnyCoreGlobal(
                            material.def,
                            neededCount,
                            material.Stuff,
                            pawn.Map
                        );
                    }
                }
            }

            if (realThing == null)
            {
                // 提取失败，跳过这个材料（Job 可能会失败）
                return;
            }

            // 销毁临时虚拟 Thing
            if (material.Spawned)
            {
                material.DeSpawn(DestroyMode.Vanish);
            }
            material.Destroy(DestroyMode.Vanish);

            // 传送到蓝图位置（有芯片）或输出接口（无芯片）
            IntVec3 spawnPos;
            bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
            if (hasChip && job.targetB.HasThing && job.targetB.Thing != null)
            {
                // 有芯片：使用蓝图位置（job.targetB 是蓝图）
                spawnPos = job.targetB.Thing.Position;
            }
            else
            {
                // 无芯片：使用输出接口位置
                spawnPos = GetSpawnPosition(pawn, sourceCore ?? gameComp.FindCoreWithItemType(material.def, material.Stuff, pawn.Map), pawn.Map);
            }
            GenSpawn.Spawn(realThing, spawnPos, pawn.Map, WipeMode.Vanish);
            FleckMaker.ThrowLightningGlow(realThing.DrawPos, realThing.Map, 0.5f);

            // 替换 Job 目标
            job.targetA = realThing;
            pawn.ClearReservationsForJob(job);

            if (DigitalStorageSettings.enableDebugLog)
            {
                string mapName = "未知地图";
                if (sourceMap != null)
                {
                    mapName = $"地图 {sourceMap.Index}";
                }
                string sourceType = virtualComp.IsFromReserved ? "预留物品" : "虚拟存储";
                Log.Message($"[数字存储] 传送建造材料: {realThing.Label} x{realThing.stackCount} 从 {sourceType} ({mapName}) 到 {pawn.Position}");
            }
        }

        /// <summary>
        /// 处理 Ingest（吃饭）- Pro Max 独有
        /// </summary>
        private static void HandleIngest(Job job, Pawn pawn)
        {
            if (!job.targetA.HasThing || job.targetA.Thing == null)
            {
                return;
            }

            Thing food = job.targetA.Thing;
            if (food.Map == null)
            {
                return;
            }

            // 检查食物是否在核心中
            Building_StorageCore core = FindCoreWithItem(food, pawn.Map);
            if (core == null)
            {
                return;
            }

            // 从虚拟存储提取并传送到 Pawn 位置
            Thing extracted = core.ExtractItem(food.def, food.stackCount, food.Stuff);
            if (extracted != null)
            {
                food.DeSpawn(DestroyMode.Vanish);
                GenSpawn.Spawn(extracted, pawn.Position, pawn.Map);
                FleckMaker.ThrowLightningGlow(extracted.DrawPos, extracted.Map, 0.5f);
                job.targetA = extracted;
            }
        }

        /// <summary>
        /// 处理 TendPatient（医疗）- 检测虚拟材料并传送
        /// </summary>
        private static void HandleTendPatient(Job job, Pawn pawn)
        {
            // TendPatient Job 使用 targetB 作为药品
            if (!job.targetB.HasThing || job.targetB.Thing == null)
            {
                return;
            }

            Thing medicine = job.targetB.Thing;
            if (medicine.Map == null)
            {
                return;
            }

            // 检查是否是虚拟材料
            CompVirtualIngredient virtualComp = null;
            if (medicine is ThingWithComps medicineWithComps)
            {
                virtualComp = medicineWithComps.GetComp<CompVirtualIngredient>();
            }
            if (virtualComp == null || !virtualComp.IsVirtual)
            {
                return; // 不是虚拟材料，跳过
            }

            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            Building_StorageCore sourceCore = virtualComp.SourceCore;
            Map sourceMap = virtualComp.SourceMap ?? pawn.Map;
            int neededCount = 1;
            Thing realThing = null;

            // 检查是否来自预留物品
            if (virtualComp.IsFromReserved && virtualComp.ReservedThing != null)
            {
                Thing reservedThing = virtualComp.ReservedThing;
                
                if (reservedThing.Spawned && reservedThing.stackCount > 0 && 
                    reservedThing.def == medicine.def && reservedThing.Stuff == medicine.Stuff)
                {
                    realThing = reservedThing.SplitOff(neededCount);
                }
            }
            else
            {
                // 完全来自虚拟存储
                if (sourceCore != null && sourceCore.Spawned && sourceCore.Powered)
                {
                    realThing = sourceCore.ExtractItem(medicine.def, neededCount, medicine.Stuff);
                }
                else
                {
                    realThing = gameComp.TryExtractItemFromAnyCoreGlobal(
                        medicine.def,
                        neededCount,
                        medicine.Stuff,
                        pawn.Map
                    );
                }
            }

            if (realThing == null)
            {
                return;
            }

            // 销毁临时虚拟 Thing
            if (medicine.Spawned)
            {
                medicine.DeSpawn(DestroyMode.Vanish);
            }
            medicine.Destroy(DestroyMode.Vanish);

            // 传送到 Pawn 位置（有芯片）或输出接口（无芯片）
            IntVec3 spawnPos = GetSpawnPosition(pawn, sourceCore ?? gameComp.FindCoreWithItemType(medicine.def, medicine.Stuff, pawn.Map), pawn.Map);
            GenSpawn.Spawn(realThing, spawnPos, pawn.Map, WipeMode.Vanish);
            FleckMaker.ThrowLightningGlow(realThing.DrawPos, realThing.Map, 0.5f);

            // 替换 Job 目标
            job.targetB = realThing;
            pawn.ClearReservationsForJob(job);

            if (DigitalStorageSettings.enableDebugLog)
            {
                string mapName = sourceMap != null ? $"地图 {sourceMap.Index}" : "未知地图";
                string sourceType = virtualComp.IsFromReserved ? "预留物品" : "虚拟存储";
                Log.Message($"[数字存储] 传送医疗材料: {realThing.Label} x{realThing.stackCount} 从 {sourceType} ({mapName})");
            }
        }

        /// <summary>
        /// 处理 Refuel（加燃料）- 检测虚拟材料并传送
        /// </summary>
        private static void HandleRefuel(Job job, Pawn pawn)
        {
            if (!job.targetA.HasThing || job.targetA.Thing == null)
            {
                return;
            }

            Thing fuel = job.targetA.Thing;
            if (fuel.Map == null)
            {
                return;
            }

            // 检查是否是虚拟材料
            CompVirtualIngredient virtualComp = null;
            if (fuel is ThingWithComps fuelWithComps)
            {
                virtualComp = fuelWithComps.GetComp<CompVirtualIngredient>();
            }
            if (virtualComp == null || !virtualComp.IsVirtual)
            {
                return; // 不是虚拟材料，跳过
            }

            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            Building_StorageCore sourceCore = virtualComp.SourceCore;
            Map sourceMap = virtualComp.SourceMap ?? pawn.Map;
            int neededCount = job.count > 0 ? job.count : fuel.stackCount;
            Thing realThing = null;

            // 检查是否来自预留物品
            if (virtualComp.IsFromReserved && virtualComp.ReservedThing != null)
            {
                Thing reservedThing = virtualComp.ReservedThing;
                
                if (reservedThing.Spawned && reservedThing.stackCount > 0 && 
                    reservedThing.def == fuel.def && reservedThing.Stuff == fuel.Stuff)
                {
                    if (reservedThing.stackCount >= neededCount)
                    {
                        realThing = reservedThing.SplitOff(neededCount);
                    }
                    else
                    {
                        realThing = reservedThing.SplitOff(reservedThing.stackCount);
                        int remaining = neededCount - realThing.stackCount;
                        
                        Thing virtualPart = null;
                        if (sourceCore != null && sourceCore.Spawned && sourceCore.Powered)
                        {
                            virtualPart = sourceCore.ExtractItem(fuel.def, remaining, fuel.Stuff);
                        }
                        else
                        {
                            virtualPart = gameComp.TryExtractItemFromAnyCoreGlobal(
                                fuel.def,
                                remaining,
                                fuel.Stuff,
                                pawn.Map
                            );
                        }
                        
                        if (virtualPart != null)
                        {
                            if (!realThing.TryAbsorbStack(virtualPart, true))
                            {
                                realThing.Destroy(DestroyMode.Vanish);
                                realThing = virtualPart;
                            }
                        }
                    }
                }
            }
            else
            {
                // 完全来自虚拟存储
                if (sourceCore != null && sourceCore.Spawned && sourceCore.Powered)
                {
                    realThing = sourceCore.ExtractItem(fuel.def, neededCount, fuel.Stuff);
                }
                else
                {
                    realThing = gameComp.TryExtractItemFromAnyCoreGlobal(
                        fuel.def,
                        neededCount,
                        fuel.Stuff,
                        pawn.Map
                    );
                }
            }

            if (realThing == null)
            {
                return;
            }

            // 销毁临时虚拟 Thing
            if (fuel.Spawned)
            {
                fuel.DeSpawn(DestroyMode.Vanish);
            }
            fuel.Destroy(DestroyMode.Vanish);

            // 传送到 Pawn 位置（有芯片）或输出接口（无芯片）
            IntVec3 spawnPos = GetSpawnPosition(pawn, sourceCore ?? gameComp.FindCoreWithItemType(fuel.def, fuel.Stuff, pawn.Map), pawn.Map);
            GenSpawn.Spawn(realThing, spawnPos, pawn.Map, WipeMode.Vanish);
            FleckMaker.ThrowLightningGlow(realThing.DrawPos, realThing.Map, 0.5f);

            // 替换 Job 目标
            job.targetA = realThing;
            pawn.ClearReservationsForJob(job);

            if (DigitalStorageSettings.enableDebugLog)
            {
                string mapName = sourceMap != null ? $"地图 {sourceMap.Index}" : "未知地图";
                string sourceType = virtualComp.IsFromReserved ? "预留物品" : "虚拟存储";
                Log.Message($"[数字存储] 传送燃料: {realThing.Label} x{realThing.stackCount} 从 {sourceType} ({mapName})");
            }
        }

        /// <summary>
        /// 处理通用 Job（支持 targetA 和 targetB）
        /// </summary>
        private static void HandleGenericJob(Job job, Pawn pawn)
        {
            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            // 检查 targetA（物品通常在 targetA）
            if (job.targetA.HasThing && job.targetA.Thing != null)
            {
                Thing item = job.targetA.Thing;
                if (item.Map != null)
                {
                    // 检查是否是虚拟材料
                    CompVirtualIngredient virtualComp = null;
                    if (item is ThingWithComps itemWithComps)
                    {
                        virtualComp = itemWithComps.GetComp<CompVirtualIngredient>();
                    }

                    if (virtualComp != null && virtualComp.IsVirtual)
                    {
                        // 处理虚拟材料（类似 HandleHaulToContainer）
                        HandleVirtualItem(job, pawn, item, virtualComp, gameComp, true);
                        return;
                    }
                }
            }

            // 检查 targetB（某些 Job 如 TendPatient 使用 targetB 作为物品）
            if (job.targetB.HasThing && job.targetB.Thing != null)
            {
                Thing item = job.targetB.Thing;
                if (item.Map != null)
                {
                    // 检查是否是虚拟材料
                    CompVirtualIngredient virtualComp = null;
                    if (item is ThingWithComps itemWithComps)
                    {
                        virtualComp = itemWithComps.GetComp<CompVirtualIngredient>();
                    }

                    if (virtualComp != null && virtualComp.IsVirtual)
                    {
                        // 处理虚拟材料
                        HandleVirtualItem(job, pawn, item, virtualComp, gameComp, false);
                        return;
                    }
                }
            }
        }

        /// <summary>
        /// 处理虚拟物品（提取并传送）
        /// </summary>
        private static void HandleVirtualItem(
            Job job,
            Pawn pawn,
            Thing material,
            CompVirtualIngredient virtualComp,
            DigitalStorageGameComponent gameComp,
            bool isTargetA)
        {
            Building_StorageCore sourceCore = virtualComp.SourceCore;
            Map sourceMap = virtualComp.SourceMap ?? pawn.Map;
            int neededCount = job.count > 0 ? job.count : material.stackCount;
            Thing realThing = null;

            // 检查是否来自预留物品
            if (virtualComp.IsFromReserved && virtualComp.ReservedThing != null)
            {
                Thing reservedThing = virtualComp.ReservedThing;
                
                if (reservedThing.Spawned && reservedThing.stackCount > 0 && 
                    reservedThing.def == material.def && reservedThing.Stuff == material.Stuff)
                {
                    if (reservedThing.stackCount >= neededCount)
                    {
                        realThing = reservedThing.SplitOff(neededCount);
                    }
                    else
                    {
                        realThing = reservedThing.SplitOff(reservedThing.stackCount);
                        int remaining = neededCount - realThing.stackCount;
                        
                        Thing virtualPart = null;
                        if (sourceCore != null && sourceCore.Spawned && sourceCore.Powered)
                        {
                            virtualPart = sourceCore.ExtractItem(material.def, remaining, material.Stuff);
                        }
                        else
                        {
                            virtualPart = gameComp.TryExtractItemFromAnyCoreGlobal(
                                material.def,
                                remaining,
                                material.Stuff,
                                pawn.Map
                            );
                        }
                        
                        if (virtualPart != null)
                        {
                            if (!realThing.TryAbsorbStack(virtualPart, true))
                            {
                                realThing.Destroy(DestroyMode.Vanish);
                                realThing = virtualPart;
                            }
                        }
                    }
                }
            }
            else
            {
                // 完全来自虚拟存储
                if (sourceCore != null && sourceCore.Spawned && sourceCore.Powered)
                {
                    realThing = sourceCore.ExtractItem(material.def, neededCount, material.Stuff);
                }
                else
                {
                    realThing = gameComp.TryExtractItemFromAnyCoreGlobal(
                        material.def,
                        neededCount,
                        material.Stuff,
                        pawn.Map
                    );
                }
            }

            if (realThing == null)
            {
                return;
            }

            // 销毁临时虚拟 Thing
            if (material.Spawned)
            {
                material.DeSpawn(DestroyMode.Vanish);
            }
            material.Destroy(DestroyMode.Vanish);

            // 传送到合适位置
            IntVec3 spawnPos = GetSpawnPosition(pawn, sourceCore ?? gameComp.FindCoreWithItemType(material.def, material.Stuff, pawn.Map), pawn.Map);
            GenSpawn.Spawn(realThing, spawnPos, pawn.Map, WipeMode.Vanish);
            FleckMaker.ThrowLightningGlow(realThing.DrawPos, realThing.Map, 0.5f);

            // 替换 Job 目标
            if (isTargetA)
            {
                job.targetA = realThing;
            }
            else
            {
                job.targetB = realThing;
            }
            pawn.ClearReservationsForJob(job);

            if (DigitalStorageSettings.enableDebugLog)
            {
                string mapName = sourceMap != null ? $"地图 {sourceMap.Index}" : "未知地图";
                string sourceType = virtualComp.IsFromReserved ? "预留物品" : "虚拟存储";
                Log.Message($"[数字存储] 传送通用 Job 材料: {realThing.Label} x{realThing.stackCount} 从 {sourceType} ({mapName})");
            }
        }

        /// <summary>
        /// 查找附近的核心
        /// </summary>
        private static List<Building_StorageCore> FindNearbyCores(IntVec3 position, Map map, float maxDistance)
        {
            List<Building_StorageCore> cores = new List<Building_StorageCore>();
            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return cores;
            }

            // 优先在当前地图查找
            List<Building_StorageCore> mapCores = gameComp.GetCoresOnMap(map);
            foreach (Building_StorageCore core in mapCores)
            {
                if (core != null && core.Spawned && core.Powered)
                {
                    float dist = IntVec3Utility.DistanceTo(core.Position, position);
                    if (dist <= maxDistance)
                    {
                        cores.Add(core);
                    }
                }
            }

            // 按距离排序
            cores.Sort((a, b) => 
                IntVec3Utility.DistanceTo(a.Position, position).CompareTo(
                IntVec3Utility.DistanceTo(b.Position, position)));

            return cores;
        }

        /// <summary>
        /// 查找包含指定物品的核心
        /// </summary>
        private static Building_StorageCore FindCoreWithItem(Thing item, List<Building_StorageCore> cores)
        {
            foreach (Building_StorageCore core in cores)
            {
                if (core != null && core.Spawned && core.Powered)
                {
                    // 检查预留物品
                    SlotGroup slotGroup = core.GetSlotGroup();
                    if (slotGroup != null)
                    {
                        foreach (Thing thing in slotGroup.HeldThings)
                        {
                            if (thing == item || (thing.def == item.def && thing.Stuff == item.Stuff))
                            {
                                return core;
                            }
                        }
                    }

                    // 检查虚拟存储
                    if (core.HasItem(item.def, item.Stuff))
                    {
                        return core;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 查找包含指定物品的核心（单地图）
        /// </summary>
        private static Building_StorageCore FindCoreWithItem(Thing item, Map map)
        {
            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return null;
            }

            // 优先在当前地图查找
            List<Building_StorageCore> mapCores = gameComp.GetCoresOnMap(map);
            foreach (Building_StorageCore core in mapCores)
            {
                if (core != null && core.Spawned && core.Powered)
                {
                    // 检查预留物品
                    SlotGroup slotGroup = core.GetSlotGroup();
                    if (slotGroup != null)
                    {
                        foreach (Thing thing in slotGroup.HeldThings)
                        {
                            if (thing == item || (thing.def == item.def && thing.Stuff == item.Stuff))
                            {
                                return core;
                            }
                        }
                    }

                    // 检查虚拟存储
                    if (core.HasItem(item.def, item.Stuff))
                    {
                        return core;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 获取物品生成位置（有芯片→脚底，没芯片→接口/核心）
        /// </summary>
        private static IntVec3 GetSpawnPosition(Pawn pawn, Building_StorageCore core, Map map)
        {
            if (PawnStorageAccess.HasTerminalImplant(pawn))
            {
                // 有芯片：生成在 Pawn 脚底
                return pawn.Position;
            }
            else
            {
                // 没芯片：生成在输出接口或核心位置
                DigitalStorageMapComponent mapComp = map?.GetComponent<DigitalStorageMapComponent>();
                if (mapComp != null && core != null)
                {
                    Building_OutputInterface nearest = FindNearestOutputInterface(pawn, core, mapComp);
                    if (nearest != null)
                    {
                        return nearest.Position;
                    }
                }
                // 如果没有输出接口，使用核心位置或 Pawn 位置
                return core?.Position ?? pawn.Position;
            }
        }

        /// <summary>
        /// 查找最近的输出接口
        /// </summary>
        private static Building_OutputInterface FindNearestOutputInterface(Pawn pawn, Building_StorageCore core, DigitalStorageMapComponent mapComp)
        {
            Building_OutputInterface nearest = null;
            float nearestDist = float.MaxValue;

            foreach (Building_OutputInterface iface in mapComp.GetAllOutputInterfaces())
            {
                if (iface == null || !iface.Spawned || iface.Map != pawn.Map)
                {
                    continue;
                }

                if (iface.BoundCore != core)
                {
                    continue;
                }

                float dist = (iface.Position - pawn.Position).LengthHorizontalSquared;
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = iface;
                }
            }

            return nearest;
        }
    }
}

