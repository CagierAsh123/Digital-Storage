// 工作台制作材料查找：补货+传送逻辑
// 核心功能：
// 1. 有芯片：补货 + 传送到工作台位置（支持跨地图）
// 2. 无芯片：补货到核心位置，让原版处理（pawn走过去拿）

using System;
using System.Collections.Generic;
using System.Linq;
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
    [HarmonyPatch(typeof(WorkGiver_DoBill), "TryFindBestBillIngredients")]
    public static class Patch_WorkGiver_DoBill_TryFindBestBillIngredients
    {
        public static void Postfix(ref bool __result, Bill bill, Pawn pawn, Thing billGiver, ref List<ThingCount> chosen)
        {
            // 如果已经找到材料，不需要处理（优先使用预留物品）
            if (__result)
            {
                return;
            }

            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            Bill_Production billProd = bill as Bill_Production;
            if (billProd == null)
            {
                return;
            }

            if (DigitalStorageSettings.enableDebugLog)
                Log.Message($"[DoBill] 开始查找材料: 配方={billProd.recipe.defName}, pawn={pawn.Name}");

            // 第一阶段：检查所有材料是否可用（核心或地图）
            List<(IngredientCount ingredient, int needed, Building_StorageCore core, ThingDef def, ThingDef stuff, Thing reservedThing, Thing mapThing, bool isCrossMap)> materialSources =
                new List<(IngredientCount, int, Building_StorageCore, ThingDef, ThingDef, Thing, Thing, bool)>();

            foreach (IngredientCount ingredient in billProd.recipe.ingredients)
            {
                ThingDef anyDef = ingredient.filter.AnyAllowedDef;
                if (anyDef == null)
                {
                    return;
                }

                int neededCount = ingredient.CountRequiredOfFor(anyDef, billProd.recipe, billProd);

                // 先查核心（本地图优先，再查跨地图）
                var coreResult = FindMaterialInCores(gameComp, ingredient, billProd, neededCount, pawn);
                if (coreResult.core != null && coreResult.def != null)
                {
                    if (DigitalStorageSettings.enableDebugLog)
                        Log.Message($"[DoBill] {anyDef.defName}: 核心有材料 (crossMap={coreResult.isCrossMap})");
                    materialSources.Add((ingredient, neededCount, coreResult.core, coreResult.def, coreResult.stuff, coreResult.reservedThing, null, coreResult.isCrossMap));
                    continue;
                }

                // 核心没有，查地图
                Thing mapThing = FindMaterialOnMap(pawn, ingredient, billProd, neededCount);
                if (mapThing != null)
                {
                    if (DigitalStorageSettings.enableDebugLog)
                        Log.Message($"[DoBill] {anyDef.defName}: 地图有材料 {mapThing.Label} at {mapThing.Position}");
                    materialSources.Add((ingredient, neededCount, null, mapThing.def, mapThing.Stuff, null, mapThing, false));
                    continue;
                }

                // 都没有，失败
                if (DigitalStorageSettings.enableDebugLog)
                    Log.Message($"[DoBill] {anyDef.defName}: 核心和地图都没有，失败");
                return;
            }

            // 第二阶段：所有材料都找到了，执行补货
            List<ThingCount> virtualIngredients = new List<ThingCount>();

            foreach (var source in materialSources)
            {
                Thing finalThing;

                if (source.core != null)
                {
                    // 跨地图时 Spawn 到工作台位置，本地图时 Spawn 到预留物品位置或核心位置
                    IntVec3 replenishPos;
                    Map spawnMap;

                    if (source.isCrossMap)
                    {
                        // 跨地图：Spawn 到 pawn 所在地图的工作台位置
                        replenishPos = billGiver.Position;
                        spawnMap = pawn.Map;
                    }
                    else
                    {
                        // 本地图：Spawn 到预留物品位置或核心位置
                        replenishPos = source.reservedThing?.Position ?? source.core.Position;
                        spawnMap = pawn.Map;
                    }

                    int maxCanCarry = pawn.carryTracker.MaxStackSpaceEver(source.def);
                    int actualCount = Math.Min(source.needed, maxCanCarry);

                    finalThing = ReplenishToNeededCount(
                        source.core, source.def, source.stuff, actualCount,
                        replenishPos, spawnMap,
                        source.isCrossMap ? null : source.reservedThing,  // 跨地图时忽略远程预留物品
                        source.isCrossMap);

                    if (finalThing == null || !finalThing.Spawned)
                    {
                        if (DigitalStorageSettings.enableDebugLog)
                            Log.Message($"[DoBill] 补货失败: {source.def.defName}");
                        return;
                    }

                    if (DigitalStorageSettings.enableDebugLog)
                        Log.Message($"[DoBill] 补货成功: {finalThing.Label} x{finalThing.stackCount} at {finalThing.Position}");
                }
                else
                {
                    // 使用地图上的材料
                    finalThing = source.mapThing;
                }

                // 检查可访问性
                if (!pawn.CanReserve(finalThing, 1, -1, null, false) || !pawn.CanReach(finalThing, PathEndMode.OnCell, Danger.Deadly))
                {
                    if (DigitalStorageSettings.enableDebugLog)
                        Log.Message($"[DoBill] 可访问性检查失败: {finalThing.Label}");
                    return;
                }

                int countToUse = Math.Min(source.needed, finalThing.stackCount);
                virtualIngredients.Add(new ThingCount(finalThing, countToUse));
            }

            // 成功
            if (virtualIngredients.Count == billProd.recipe.ingredients.Count)
            {
                if (chosen == null)
                {
                    chosen = new List<ThingCount>();
                }
                chosen.AddRange(virtualIngredients);
                __result = true;

                if (DigitalStorageSettings.enableDebugLog)
                    Log.Message($"[DoBill] 成功找到 {virtualIngredients.Count} 种材料");
            }
        }

        private static Thing FindMaterialOnMap(Pawn pawn, IngredientCount ingredient, Bill_Production billProd, int neededCount)
        {
            return GenClosest.ClosestThingReachable(
                pawn.Position,
                pawn.Map,
                ThingRequest.ForGroup(ThingRequestGroup.HaulableEver),
                PathEndMode.ClosestTouch,
                TraverseParms.For(pawn, Danger.Deadly, TraverseMode.ByPawn, false, false, true),
                9999f,
                t => t.Spawned &&
                     ingredient.filter.Allows(t) &&
                     (billProd.ingredientFilter == null || billProd.ingredientFilter.Allows(t) || ingredient.IsFixedIngredient) &&
                     !t.IsForbidden(pawn) &&
                     pawn.CanReserve(t, 1, -1, null, false) &&
                     t.stackCount >= neededCount &&
                     !t.IsNotFresh(),
                null, 0, -1, false, RegionType.Set_Passable, false, false);
        }

        /// <summary>
        /// 在所有核心中查找材料。本地图核心优先，跨地图核心需要芯片。
        /// </summary>
        private static (Building_StorageCore core, ThingDef def, ThingDef stuff, Thing reservedThing, bool isCrossMap) FindMaterialInCores(
            DigitalStorageGameComponent gameComp, IngredientCount ingredient, Bill_Production billProd, int neededCount, Pawn pawn)
        {
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

                    // 本地图：先检查预留物品
                    if (!isCrossMap)
                    {
                        ThingDef anyDef = ingredient.filter.AnyAllowedDef;
                        Thing reservedThing = core.FindReservedItem(anyDef, null);

                        if (reservedThing != null && reservedThing.Spawned)
                        {
                            if (!ingredient.filter.Allows(reservedThing.def))
                            {
                                goto checkVirtual;
                            }

                            bool billFilterAllows = billProd.ingredientFilter == null ||
                                                   billProd.ingredientFilter.Allows(reservedThing.def) ||
                                                   ingredient.filter.AllowedDefCount == 1;

                            if (billFilterAllows)
                            {
                                int reservedCount = reservedThing.stackCount;
                                int virtualCount = core.GetItemCount(reservedThing.def, reservedThing.Stuff);

                                if (reservedCount + virtualCount >= neededCount)
                                {
                                    return (core, reservedThing.def, reservedThing.Stuff, reservedThing, false);
                                }
                            }
                        }
                    }

                    checkVirtual:
                    // 检查虚拟存储
                    foreach (StoredItemData itemData in core.GetAllStoredItems())
                    {
                        if (itemData.def == null || itemData.stackCount <= 0)
                        {
                            continue;
                        }

                        if (!ingredient.filter.Allows(itemData.def))
                        {
                            continue;
                        }

                        bool billFilterAllows = billProd.ingredientFilter == null ||
                                               billProd.ingredientFilter.Allows(itemData.def) ||
                                               ingredient.filter.AllowedDefCount == 1;

                        if (billFilterAllows && itemData.stackCount >= neededCount)
                        {
                            return (core, itemData.def, itemData.stuffDef, null, isCrossMap);
                        }
                    }
                }
            }

            return (null, null, null, null, false);
        }

        /// <summary>
        /// 从核心提取物品并 Spawn 到指定位置
        /// isCrossMap=true 时忽略 existingReserved（远程地图的预留物品不在本地图）
        /// </summary>
        private static Thing ReplenishToNeededCount(Building_StorageCore core, ThingDef def, ThingDef stuff,
            int needed, IntVec3 position, Map map, Thing existingReserved, bool isCrossMap)
        {
            if (core == null || def == null || needed <= 0 || map == null)
            {
                return null;
            }

            // 跨地图：直接从虚拟存储提取全部数量
            if (isCrossMap)
            {
                Thing extracted = core.ExtractItem(def, needed, stuff);
                if (extracted == null)
                {
                    return null;
                }

                GenSpawn.Spawn(extracted, position, map, WipeMode.Vanish);
                return extracted;
            }

            // 本地图：原有逻辑
            int currentCount = existingReserved?.stackCount ?? 0;
            int needMore = needed - currentCount;

            if (needMore <= 0)
            {
                return existingReserved;
            }

            Thing extractedLocal = core.ExtractItem(def, needMore, stuff);
            if (extractedLocal == null)
            {
                return existingReserved;
            }

            if (existingReserved != null && existingReserved.Spawned)
            {
                bool absorbed = existingReserved.TryAbsorbStack(extractedLocal, false);
                if (absorbed)
                {
                    try
                    {
                        if (extractedLocal != null && !extractedLocal.Destroyed && extractedLocal.stackCount > 0)
                        {
                            GenSpawn.Spawn(extractedLocal, existingReserved.Position, map, WipeMode.Vanish);
                        }
                    }
                    catch { }
                    return existingReserved;
                }
                else
                {
                    GenSpawn.Spawn(extractedLocal, existingReserved.Position, map, WipeMode.Vanish);
                    return existingReserved;
                }
            }
            else
            {
                GenSpawn.Spawn(extractedLocal, position, map, WipeMode.Vanish);
                return extractedLocal;
            }
        }

        private static Thing FindMaterialOnMapFallback(Pawn pawn, IngredientCount ingredient, Bill_Production billProd, int neededCount)
        {
            return GenClosest.ClosestThingReachable(
                pawn.Position,
                pawn.Map,
                ThingRequest.ForGroup(ThingRequestGroup.HaulableEver),
                PathEndMode.ClosestTouch,
                TraverseParms.For(pawn, Danger.Deadly, TraverseMode.ByPawn, false, false, true),
                9999f,
                t => t.Spawned &&
                     ingredient.filter.Allows(t) &&
                     (billProd.ingredientFilter == null || billProd.ingredientFilter.Allows(t) || ingredient.IsFixedIngredient) &&
                     !t.IsForbidden(pawn) &&
                     pawn.CanReserve(t, 1, -1, null, false) &&
                     t.stackCount >= neededCount &&
                     !t.IsNotFresh(),
                null, 0, -1, false, RegionType.Set_Passable, false, false);
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
    }
}
