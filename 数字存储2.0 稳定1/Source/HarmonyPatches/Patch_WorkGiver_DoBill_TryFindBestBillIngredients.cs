// 工作台制作材料查找：补货+传送逻辑
// 核心功能：参照建造系统，先补货再传送
// 实现步骤：
// 1. Postfix：原版找不到材料时，从虚拟存储补货到地图
// 2. 如果有芯片，传送到工作台位置；如果有接口且接口更近，传送到接口位置
// 3. 将补货并传送后的真实物品添加到 chosen 列表，让原版创建Job去拿

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
    /// 工作台制作材料查找：补货+传送逻辑
    /// 核心逻辑：参照建造系统，先补货（从虚拟存储提取真实物品），再传送到工作台位置（如果有芯片）
    /// </summary>
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

            Bill_Production billProd = bill as Bill_Production;
            if (billProd == null)
            {
                return;
            }

            List<ThingCount> virtualIngredients = new List<ThingCount>();

            foreach (IngredientCount ingredient in billProd.recipe.ingredients)
            {
                // 计算需要的数量
                ThingDef anyDef = ingredient.filter.AnyAllowedDef;
                if (anyDef == null)
                {
                    return; // 无法确定材料类型
                }

                int neededCount = ingredient.CountRequiredOfFor(
                    anyDef,
                    billProd.recipe,
                    billProd
                );

                // 优先检查预留物品，然后检查虚拟存储
                ThingDef foundDef = null;
                ThingDef foundStuff = null;
                Building_StorageCore foundCore = null;
                Thing foundReservedThing = null;
                int reservedCount = 0;
                int virtualCount = 0;
                bool useReserved = false;

                // 遍历所有核心，查找匹配的材料
                foreach (Building_StorageCore core in gameComp.GetAllCores())
                {
                    if (core == null || !core.Spawned || !core.Powered)
                    {
                        continue;
                    }

                    // 先检查预留物品
                    Thing reservedThing = core.FindReservedItem(anyDef, null);
                    if (reservedThing != null && reservedThing.Spawned)
                    {
                        // 检查是否匹配 ingredient.filter
                        if (ingredient.filter.Allows(reservedThing.def))
                        {
                            // 检查是否匹配 bill.ingredientFilter
                            if (billProd.ingredientFilter == null || billProd.ingredientFilter.Allows(reservedThing.def))
                            {
                                reservedCount = reservedThing.stackCount;
                                if (reservedCount >= neededCount)
                                {
                                    // 预留物品足够，优先使用预留物品
                                    foundDef = reservedThing.def;
                                    foundStuff = reservedThing.Stuff;
                                    foundCore = core;
                                    foundReservedThing = reservedThing;
                                    useReserved = true;
                                    break;
                                }
                                else
                                {
                                    // 预留物品不够，记录信息，继续查找虚拟存储补足
                                    foundDef = reservedThing.def;
                                    foundStuff = reservedThing.Stuff;
                                    foundCore = core;
                                    foundReservedThing = reservedThing;
                                    virtualCount = neededCount - reservedCount;
                                }
                            }
                        }
                    }

                    // 如果预留物品不够，查找虚拟存储补足
                    if (!useReserved && foundCore != null && virtualCount > 0)
                    {
                        bool foundVirtualEnough = false;
                        // 遍历虚拟存储中的物品
                        foreach (StoredItemData itemData in core.GetAllStoredItems())
                        {
                            if (itemData.def == null || itemData.stackCount <= 0)
                            {
                                continue;
                            }

                            // 检查是否匹配 ingredient.filter
                            if (!ingredient.filter.Allows(itemData.def))
                            {
                                continue;
                            }

                            // 检查是否匹配 bill.ingredientFilter
                            if (billProd.ingredientFilter != null && !billProd.ingredientFilter.Allows(itemData.def))
                            {
                                continue;
                            }

                            // 检查是否匹配预留物品的类型
                            if (foundDef != null && (itemData.def != foundDef || itemData.stuffDef != foundStuff))
                            {
                                continue;
                            }

                            // 检查数量是否足够补足
                            if (itemData.stackCount >= virtualCount)
                            {
                                // 找到足够的虚拟物品补足
                                foundVirtualEnough = true;
                                break;
                            }
                        }
                        
                        // 如果虚拟存储不够补足，重置 foundCore，继续查找其他核心
                        if (!foundVirtualEnough)
                        {
                            foundCore = null;
                            foundDef = null;
                            foundStuff = null;
                            foundReservedThing = null;
                            virtualCount = 0;
                        }
                    }
                    else if (!useReserved && foundCore == null)
                    {
                        // 没有预留物品，查找虚拟存储
                        foreach (StoredItemData itemData in core.GetAllStoredItems())
                        {
                            if (itemData.def == null || itemData.stackCount <= 0)
                            {
                                continue;
                            }

                            // 检查是否匹配 ingredient.filter
                            if (!ingredient.filter.Allows(itemData.def))
                            {
                                continue;
                            }

                            // 检查是否匹配 bill.ingredientFilter
                            if (billProd.ingredientFilter != null && !billProd.ingredientFilter.Allows(itemData.def))
                            {
                                continue;
                            }

                            // 检查数量是否足够
                            if (itemData.stackCount >= neededCount)
                            {
                                foundDef = itemData.def;
                                foundStuff = itemData.stuffDef;
                                foundCore = core;
                                virtualCount = neededCount;
                                break;
                            }
                        }
                    }

                    if (foundCore != null && (useReserved || virtualCount > 0))
                    {
                        break;
                    }
                }

                // 如果找不到匹配的材料，返回失败
                if (foundCore == null || foundDef == null)
                {
                    return;
                }

                // 确定补货位置（优先预留物品位置，其次工作台位置）
                IntVec3 replenishPos;
                if (foundReservedThing != null && foundReservedThing.Spawned)
                {
                    replenishPos = foundReservedThing.Position;
                }
                else
                {
                    replenishPos = GetBillGiverRootCell(billGiver, pawn);
                }

                // 限制补货数量为pawn一次能拿的数量
                int maxCanCarry = pawn.carryTracker.MaxStackSpaceEver(foundDef);
                int actualReplenishCount = Math.Min(neededCount, maxCanCarry);

                // 补货到需要的数量
                Thing replenishedThing = ReplenishToNeededCount(foundCore, foundDef, foundStuff, actualReplenishCount, replenishPos, pawn.Map, foundReservedThing);

                if (replenishedThing == null || !replenishedThing.Spawned)
                {
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Warning($"[数字存储] TryFindBestBillIngredients: 补货失败, 材料={foundDef.label}, 需要={actualReplenishCount}");
                    }
                    return; // 补货失败，返回失败
                }

                // 确定传送目标位置（有芯片传送到工作台，无芯片有接口传送到接口）
                IntVec3? teleportPos = DetermineTeleportPositionForWorkbench(pawn, foundCore, billGiver, hasChip, hasInterface);

                // 如果需要传送且位置不同，执行传送
                if (teleportPos.HasValue && teleportPos.Value != replenishedThing.Position)
                {
                    replenishedThing = TeleportReplenishedItemToTarget(replenishedThing, teleportPos.Value, pawn.Map);

                    if (replenishedThing == null)
                    {
                        if (DigitalStorageSettings.enableDebugLog)
                        {
                            Log.Warning($"[数字存储] TryFindBestBillIngredients: 传送失败");
                        }
                        return; // 传送失败，返回失败
                    }

                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Message($"[数字存储] TryFindBestBillIngredients: 补货并传送完成, 材料={foundDef.label}, 数量={replenishedThing.stackCount}, 从 {replenishPos} 传送到 {teleportPos.Value}");
                    }
                }
                else if (DigitalStorageSettings.enableDebugLog)
                {
                    Log.Message($"[数字存储] TryFindBestBillIngredients: 补货完成，无需传送, 材料={foundDef.label}, 数量={replenishedThing.stackCount}, 位置={replenishedThing.Position}");
                }

                // 检查 Pawn 是否可以访问补货后的物品
                if (!pawn.CanReserve(replenishedThing, 1, -1, null, false) || !pawn.CanReach(replenishedThing, PathEndMode.OnCell, Danger.Deadly))
                {
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Warning($"[数字存储] TryFindBestBillIngredients: Pawn无法访问补货后的物品, {replenishedThing.Label}");
                    }
                    return; // 无法访问，返回失败
                }

                // 添加到chosen列表，让原版创建Job去拿这些材料
                virtualIngredients.Add(new ThingCount(replenishedThing, actualReplenishCount));
            }

            // 如果找到了所有材料，注入到 chosen 列表
            if (virtualIngredients.Count == billProd.recipe.ingredients.Count)
            {
                if (chosen == null)
                {
                    chosen = new List<ThingCount>();
                }
                chosen.AddRange(virtualIngredients);
                __result = true; // 告诉原版找到了材料

                if (DigitalStorageSettings.enableDebugLog)
                {
                    Log.Message($"[数字存储] TryFindBestBillIngredients: 补货并传送完成，找到 {virtualIngredients.Count} 种材料");
                }
            }
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
        /// 获取工作台的根位置（复制自原版 WorkGiver_DoBill.GetBillGiverRootCell）
        /// </summary>
        private static IntVec3 GetBillGiverRootCell(Thing billGiver, Pawn forPawn)
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
            Log.Error("Tried to find bill ingredients for " + ((billGiver != null) ? billGiver.ToString() : null) + " which has no interaction cell.");
            return forPawn.Position;
        }

        /// <summary>
        /// 补货到需要的数量（复用建造系统的逻辑）
        /// </summary>
        private static Thing ReplenishToNeededCount(Building_StorageCore core, ThingDef def, ThingDef stuff, int needed, IntVec3 position, Map map, Thing existingReserved)
        {
            if (core == null || def == null || needed <= 0 || map == null)
            {
                return null;
            }

            int currentCount = existingReserved?.stackCount ?? 0;
            int needMore = needed - currentCount;

            if (needMore <= 0)
            {
                // 预留物品已经足够，直接返回
                return existingReserved;
            }

            // 从虚拟存储提取需要的数量
            Thing extracted = core.ExtractItem(def, needMore, stuff);
            if (extracted == null)
            {
                if (DigitalStorageSettings.enableDebugLog)
                {
                    Log.Warning($"[数字存储] ReplenishToNeededCount: 虚拟存储不足，无法补货, 需要 {def.label} x{needMore}");
                }
                return existingReserved; // 返回现有的预留物品
            }

            // 如果有现有预留物品，合并堆栈
            if (existingReserved != null && existingReserved.Spawned)
            {
                bool absorbed = existingReserved.TryAbsorbStack(extracted, false);
                
                if (absorbed)
                {
                    // 合并成功
                    // TryAbsorbStack的行为：
                    // - 如果完全吸收，extracted会被销毁
                    // - 如果部分吸收，extracted会保留剩余数量
                    // 安全检查：只有在extracted还存在且有剩余时才spawn
                    try
                    {
                        if (extracted != null && !extracted.Destroyed && extracted.stackCount > 0)
                        {
                            GenSpawn.Spawn(extracted, existingReserved.Position, map, WipeMode.Vanish);
                        }
                    }
                    catch
                    {
                        // extracted已被销毁，忽略
                    }
                    return existingReserved;
                }
                else
                {
                    // 无法合并（堆叠限制），在预留物品位置spawn新物品
                    GenSpawn.Spawn(extracted, existingReserved.Position, map, WipeMode.Vanish);
                    return existingReserved; // 返回预留物品（提取的物品在旁边）
                }
            }
            else
            {
                // 没有预留物品，在指定位置spawn
                GenSpawn.Spawn(extracted, position, map, WipeMode.Vanish);
                return extracted;
            }
        }

        /// <summary>
        /// 确定传送目标位置（工作台版本）
        /// </summary>
        private static IntVec3? DetermineTeleportPositionForWorkbench(Pawn pawn, Building_StorageCore core, Thing billGiver, bool hasChip, bool hasInterface)
        {
            if (hasChip)
            {
                // 有芯片：传送到工作台位置
                IntVec3 workbenchPos = GetBillGiverRootCell(billGiver, pawn);
                return workbenchPos;
            }
            else if (hasInterface)
            {
                // 无芯片：比较接口和工作台的距离
                Building_OutputInterface nearestInterface = FindNearestOutputInterface(pawn.Map, core);
                if (nearestInterface != null && nearestInterface.Spawned)
                {
                    IntVec3 workbenchPos = GetBillGiverRootCell(billGiver, pawn);
                    float interfaceDistance = (nearestInterface.Position - workbenchPos).LengthHorizontalSquared;
                    // 计算核心到工作台位置的距离
                    float coreDistance = (core.Position - workbenchPos).LengthHorizontalSquared;
                    
                    if (interfaceDistance < coreDistance)
                    {
                        // 接口更近，传送到接口位置
                        return nearestInterface.Position;
                    }
                    // 核心更近，不传送（让pawn走过去）
                    return null;
                }
            }
            
            // 无接口，不传送
            return null;
        }

        /// <summary>
        /// 传送补货后的物品到目标位置（复用建造系统的逻辑）
        /// </summary>
        private static Thing TeleportReplenishedItemToTarget(Thing item, IntVec3 targetPos, Map map)
        {
            if (item == null || !item.Spawned || map == null)
            {
                return null;
            }

            IntVec3 originalPos = item.Position;
            
            // DeSpawn原物品
            item.DeSpawn(0);
            
            // Spawn到目标位置
            Thing spawned = GenSpawn.Spawn(item, targetPos, map, 0);
            
            // 添加闪电效果
            FleckMaker.ThrowLightningGlow(spawned.DrawPos, map, 0.5f);
            
            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] TeleportReplenishedItemToTarget: 传送完成, {spawned.Label} x{spawned.stackCount} 从 {originalPos} 到 {targetPos}");
            }
            
            return spawned;
        }

        /// <summary>
        /// 查找最近的输出接口（复用建造系统的逻辑）
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
    }
}

