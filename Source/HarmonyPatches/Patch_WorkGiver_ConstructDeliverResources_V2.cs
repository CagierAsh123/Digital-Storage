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
    /// 3. 跨地图：有芯片时从远程核心提取，Spawn 到蓝图位置
    /// 注意：这里只补货，不传送。传送在 StartJob 阶段处理（有芯片时）
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "ResourceDeliverJobFor")]
    public static class Patch_WorkGiver_ConstructDeliverResources_V2
    {
        // 使用设置中的日志开关
        private static bool LOG => DigitalStorageSettings.enableDebugLog;

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
            if (__result != null && __result.def == JobDefOf.HaulToContainer)
            {
                if (LOG) Log.Message($"[建造] 情况1: 原版找到工作，跳过");
                return;
            }

            // 情况2：原版没找到工作（预留不够或没有预留）
            if (__result != null)
            {
                if (LOG) Log.Message($"[建造] 情况2: 原版找到其他类型工作，跳过");
                return;
            }

            // 安全检查
            IConstructible constructible = c as IConstructible;
            if (constructible == null)
            {
                if (LOG) Log.Message($"[建造] c 不是 IConstructible，跳过");
                return;
            }

            if (LOG) Log.Message($"[建造] 原版没找到工作，尝试从核心获取");

            // 为第一个可处理的材料创建job
            foreach (ThingDefCountClass need in constructible.TotalMaterialCost())
            {
                int countNeeded = GetCountNeeded(c, need.thingDef, pawn, forced);
                if (LOG) Log.Message($"[建造] 检查材料 {need.thingDef.defName}, countNeeded={countNeeded}");
                if (countNeeded <= 0)
                {
                    continue;
                }

                // 限制为pawn携带上限
                int maxCanCarry = pawn.carryTracker.MaxStackSpaceEver(need.thingDef);
                int actualNeeded = Math.Min(countNeeded, maxCanCarry);
                if (LOG) Log.Message($"[建造] maxCanCarry={maxCanCarry}, actualNeeded={actualNeeded}");

                // 蓝图位置（用于跨地图 Spawn）
                Thing blueprint = c as Thing;
                IntVec3 blueprintPos = blueprint?.Position ?? pawn.Position;

                // 查找材料来源并获取/补货
                Thing finalThing = GetOrReplenishMaterial(gameComp, need.thingDef, actualNeeded, pawn, blueprintPos);
                
                if (finalThing == null || !finalThing.Spawned)
                {
                    if (LOG) Log.Message($"[建造] GetOrReplenishMaterial 返回 null");
                    continue;
                }

                if (LOG) Log.Message($"[建造] 获取到材料 {finalThing.Label} x{finalThing.stackCount} at {finalThing.Position}");

                // 创建Job
                __result = CreateHaulJob(pawn, c, finalThing, actualNeeded);
                if (LOG) Log.Message($"[建造] 创建Job, __result={__result != null}");
                return;
            }
        }

        /// <summary>
        /// 获取或补货材料
        /// 本地图核心：走预留+补货逻辑
        /// 跨地图核心：有芯片时直接从虚拟存储提取，Spawn 到蓝图位置
        /// </summary>
        private static Thing GetOrReplenishMaterial(DigitalStorageGameComponent gameComp, ThingDef def, int needed, Pawn pawn, IntVec3 blueprintPos)
        {
            if (LOG) Log.Message($"[建造] GetOrReplenishMaterial: def={def.defName}, needed={needed}");

            bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
            Map pawnMap = pawn.Map;

            // 两轮遍历：第一轮本地图，第二轮跨地图
            for (int pass = 0; pass < 2; pass++)
            {
                foreach (Building_StorageCore core in gameComp.GetAllCores())
                {
                    if (core == null || !core.Spawned || !core.Powered)
                    {
                        continue;
                    }

                    bool isCrossMap = core.Map != pawnMap;

                    // 第一轮只查本地图，第二轮只查跨地图
                    if (pass == 0 && isCrossMap) continue;
                    if (pass == 1 && !isCrossMap) continue;

                    // 跨地图必须有芯片
                    if (isCrossMap && !hasChip)
                    {
                        continue;
                    }

                    if (LOG) Log.Message($"[建造] 检查核心={core.NetworkName}, crossMap={isCrossMap}");

                    if (isCrossMap)
                    {
                        // ===== 跨地图逻辑 =====
                        // 只查虚拟存储（远程核心的预留物品在另一张地图上，本地 pawn 拿不到）
                        int virtualCount = core.GetVirtualItemCount(def);

                        if (LOG) Log.Message($"[建造] 跨地图: virtualCount={virtualCount}");

                        if (virtualCount < needed)
                        {
                            continue;
                        }

                        Thing extracted = core.ExtractItem(def, needed, null);
                        if (extracted == null)
                        {
                            if (LOG) Log.Message($"[建造] 跨地图: ExtractItem 返回 null");
                            continue;
                        }

                        // Spawn 到 pawn 所在地图的蓝图位置
                        GenSpawn.Spawn(extracted, blueprintPos, pawnMap, WipeMode.Vanish);

                        if (LOG) Log.Message($"[建造] 跨地图: Spawn {extracted.Label} x{extracted.stackCount} at {blueprintPos}");

                        return extracted;
                    }
                    else
                    {
                        // ===== 本地图逻辑（原有逻辑不变） =====
                        Thing reservedThing = core.FindReservedItem(def, null);
                        int reservedCount = reservedThing?.stackCount ?? 0;
                        int virtualCount = core.GetVirtualItemCount(def);
                        int totalAvailable = reservedCount + virtualCount;

                        if (LOG) Log.Message($"[建造] 本地图: reservedCount={reservedCount}, virtualCount={virtualCount}, totalAvailable={totalAvailable}");

                        if (totalAvailable < needed)
                        {
                            if (LOG) Log.Message($"[建造] 本地图: 材料不足，跳过");
                            continue;
                        }

                        Thing finalThing;

                        if (reservedCount >= needed)
                        {
                            if (LOG) Log.Message($"[建造] 本地图: 预留足够，从预留分出 {needed} 个");
                            if (reservedCount == needed)
                            {
                                finalThing = reservedThing;
                            }
                            else
                            {
                                finalThing = reservedThing.SplitOff(needed);
                                GenSpawn.Spawn(finalThing, reservedThing.Position, pawnMap, WipeMode.Vanish);
                            }
                        }
                        else
                        {
                            if (LOG) Log.Message($"[建造] 本地图: 预留不够，需要补货 {needed - reservedCount} 个");
                            int needMore = needed - reservedCount;
                            Thing extracted = core.ExtractItem(def, needMore, null);
                            
                            if (extracted == null)
                            {
                                if (LOG) Log.Message($"[建造] 本地图: ExtractItem 返回 null");
                                continue;
                            }

                            if (LOG) Log.Message($"[建造] 本地图: 提取到 {extracted.Label} x{extracted.stackCount}");

                            if (reservedThing != null && reservedThing.Spawned)
                            {
                                reservedThing.TryAbsorbStack(extracted, false);
                                if (extracted.stackCount > 0)
                                {
                                    GenSpawn.Spawn(extracted, reservedThing.Position, pawnMap, WipeMode.Vanish);
                                }
                                finalThing = reservedThing;
                            }
                            else
                            {
                                GenSpawn.Spawn(extracted, core.Position, pawnMap, WipeMode.Vanish);
                                finalThing = extracted;
                            }
                        }

                        if (LOG) Log.Message($"[建造] 本地图: finalThing={finalThing.Label} x{finalThing.stackCount} at {finalThing.Position}");

                        return finalThing;
                    }
                }
            }

            if (LOG) Log.Message($"[建造] 没有找到合适的核心");
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
