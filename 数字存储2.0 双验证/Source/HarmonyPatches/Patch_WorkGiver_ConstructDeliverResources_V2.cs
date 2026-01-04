using System;
using System.Collections.Generic;
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
    /// 蓝图建造材料查找：统一处理预留足够和补货两种情况
    /// 1. 预留足够：从预留分出需要的数量
    /// 2. 预留不够：补货后合并
    /// 注意：这里只补货，不传送。传送在 StartJob 阶段处理（有芯片时）
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "ResourceDeliverJobFor")]
    public static class Patch_WorkGiver_ConstructDeliverResources_V2
    {
        // 专用日志开关
        private static bool LOG = true;

        public static void Postfix(
            ref Job __result,
            WorkGiver_ConstructDeliverResources __instance,
            Pawn pawn,
            IConstructible c,
            bool canRemoveExistingFloorUnderNearbyNeeders,
            bool forced)
        {
            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);

            if (LOG) Log.Message($"[建造] Postfix开始: pawn={pawn.Name}, hasChip={hasChip}, __result={__result != null}");

            // 情况1：原版已经找到工作（预留物品足够）
            // 注意：这里不再调用 FindCoreWithItem，因为物品已被情况2处理过
            if (__result != null && __result.def == JobDefOf.HaulToContainer)
            {
                if (LOG) Log.Message($"[建造] 情况1: 原版找到工作，跳过");
                return;
            }

            // 情况2：原版没找到工作（预留不够或没有预留）
            if (__result != null)
            {
                if (LOG) Log.Message($"[建造] 原版找到其他类型工作，跳过");
                return;
            }

            if (LOG) Log.Message($"[建造] 情况2: 原版没找到工作，尝试从核心获取");

            // 为第一个可处理的材料创建job
            foreach (ThingDefCountClass need in c.TotalMaterialCost())
            {
                int countNeeded = GetCountNeeded(c, need.thingDef, pawn, forced);
                if (LOG) Log.Message($"[建造] 情况2: 检查材料 {need.thingDef.defName}, countNeeded={countNeeded}");
                if (countNeeded <= 0)
                {
                    continue;
                }

                // 限制为pawn携带上限
                int maxCanCarry = pawn.carryTracker.MaxStackSpaceEver(need.thingDef);
                int actualNeeded = Math.Min(countNeeded, maxCanCarry);
                if (LOG) Log.Message($"[建造] 情况2: maxCanCarry={maxCanCarry}, actualNeeded={actualNeeded}");

                // 查找材料来源并获取/补货（不传送，传送在 StartJob 阶段处理）
                Thing finalThing = GetOrReplenishMaterial(gameComp, need.thingDef, actualNeeded, pawn.Map);
                
                if (finalThing == null || !finalThing.Spawned)
                {
                    if (LOG) Log.Message($"[建造] 情况2: GetOrReplenishMaterial 返回 null");
                    continue;
                }

                if (LOG) Log.Message($"[建造] 情况2: 获取到材料 {finalThing.Label} x{finalThing.stackCount} at {finalThing.Position}");

                // 创建Job
                __result = CreateHaulJob(pawn, c, finalThing, actualNeeded);
                if (LOG) Log.Message($"[建造] 情况2: 创建Job, __result={__result != null}");
                return;
            }
        }

        /// <summary>
        /// 获取或补货材料（只补货，不传送）
        /// </summary>
        private static Thing GetOrReplenishMaterial(DigitalStorageGameComponent gameComp, ThingDef def, int needed, Map map)
        {
            if (LOG) Log.Message($"[建造] GetOrReplenishMaterial: def={def.defName}, needed={needed}");

            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered || core.Map != map)
                {
                    continue;
                }

                // 检查预留物品
                Thing reservedThing = core.FindReservedItem(def, null);
                int reservedCount = reservedThing?.stackCount ?? 0;
                int virtualCount = core.GetItemCount(def, null);
                int totalAvailable = reservedCount + virtualCount;

                if (LOG) Log.Message($"[建造] GetOrReplenishMaterial: 核心={core.NetworkName}, reservedCount={reservedCount}, virtualCount={virtualCount}, totalAvailable={totalAvailable}");

                if (totalAvailable < needed)
                {
                    if (LOG) Log.Message($"[建造] GetOrReplenishMaterial: 核心材料不足，跳过");
                    continue; // 这个核心没有足够的材料
                }

                Thing finalThing;

                if (reservedCount >= needed)
                {
                    if (LOG) Log.Message($"[建造] GetOrReplenishMaterial: 预留足够，从预留分出 {needed} 个");
                    // 预留足够：从预留分出需要的数量
                    if (reservedCount == needed)
                    {
                        finalThing = reservedThing;
                    }
                    else
                    {
                        finalThing = reservedThing.SplitOff(needed);
                        GenSpawn.Spawn(finalThing, reservedThing.Position, map, WipeMode.Vanish);
                    }
                }
                else
                {
                    if (LOG) Log.Message($"[建造] GetOrReplenishMaterial: 预留不够，需要补货 {needed - reservedCount} 个");
                    // 预留不够：补货
                    int needMore = needed - reservedCount;
                    Thing extracted = core.ExtractItem(def, needMore, null);
                    
                    if (extracted == null)
                    {
                        if (LOG) Log.Message($"[建造] GetOrReplenishMaterial: ExtractItem 返回 null");
                        continue;
                    }

                    if (LOG) Log.Message($"[建造] GetOrReplenishMaterial: 提取到 {extracted.Label} x{extracted.stackCount}");

                    if (reservedThing != null && reservedThing.Spawned)
                    {
                        // 合并到预留物品
                        if (LOG) Log.Message($"[建造] GetOrReplenishMaterial: 合并到预留物品");
                        reservedThing.TryAbsorbStack(extracted, false);
                        if (extracted.stackCount > 0)
                        {
                            if (LOG) Log.Message($"[建造] GetOrReplenishMaterial: 合并后剩余 {extracted.stackCount}，生成到核心位置");
                            GenSpawn.Spawn(extracted, reservedThing.Position, map, WipeMode.Vanish);
                        }
                        finalThing = reservedThing;
                    }
                    else
                    {
                        // 没有预留，直接生成
                        if (LOG) Log.Message($"[建造] GetOrReplenishMaterial: 没有预留，直接生成");
                        GenSpawn.Spawn(extracted, core.Position, map, WipeMode.Vanish);
                        finalThing = extracted;
                    }
                }

                if (LOG) Log.Message($"[建造] GetOrReplenishMaterial: finalThing={finalThing.Label} x{finalThing.stackCount} at {finalThing.Position}");

                // 不在这里传送，传送在 StartJob 阶段处理

                return finalThing;
            }

            if (LOG) Log.Message($"[建造] GetOrReplenishMaterial: 没有找到合适的核心");
            return null;
        }

        /// <summary>
        /// 从物品中分出指定数量并传送
        /// </summary>
        private static Thing SplitAndTeleport(Thing source, int count, IntVec3 targetPos, Map map)
        {
            if (source == null || !source.Spawned)
            {
                return null;
            }

            Thing thingToTeleport;
            if (source.stackCount <= count)
            {
                source.DeSpawn(0);
                thingToTeleport = source;
            }
            else
            {
                thingToTeleport = source.SplitOff(count);
            }

            Thing spawned = GenSpawn.Spawn(thingToTeleport, targetPos, map, WipeMode.Vanish);
            FleckMaker.ThrowLightningGlow(spawned.DrawPos, map, 0.5f);

            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] 传送物品: {spawned.Label} x{spawned.stackCount} 到 {targetPos}");
            }

            return spawned;
        }

        private static Building_StorageCore FindCoreWithItem(DigitalStorageGameComponent gameComp, Thing item)
        {
            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered)
                {
                    continue;
                }

                SlotGroup slotGroup = core.GetSlotGroup();
                if (slotGroup != null)
                {
                    foreach (Thing thing in slotGroup.HeldThings)
                    {
                        if (thing == item)
                        {
                            return core;
                        }
                    }
                }
            }
            return null;
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

        private static Thing TeleportItem(Thing item, IntVec3 targetPos, Map map)
        {
            if (item == null || !item.Spawned || map == null)
            {
                return null;
            }

            item.DeSpawn(0);
            Thing spawned = GenSpawn.Spawn(item, targetPos, map, 0);
            FleckMaker.ThrowLightningGlow(spawned.DrawPos, map, 0.5f);
            
            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] 传送物品: {spawned.Label} x{spawned.stackCount} 到 {targetPos}");
            }
            
            return spawned;
        }

        private static Job CreateHaulJob(Pawn pawn, IConstructible c, Thing realThing, int countNeeded)
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

            if (!pawn.Map.reservationManager.Reserve(pawn, job, realThing, 1, -1, null, false, false))
            {
                return null;
            }

            return job;
        }
    }
}
