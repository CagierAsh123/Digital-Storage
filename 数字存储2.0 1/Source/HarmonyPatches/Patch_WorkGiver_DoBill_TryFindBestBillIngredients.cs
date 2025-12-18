// TODO: 待实现 - 新架构
// 核心功能：在 TryFindBestBillIngredients 时注入虚拟 Thing
// 实现步骤：
// 1. Postfix：原版找不到材料时，从虚拟存储查找（跨所有地图）
// 2. 创建临时 Thing，设置 position = 工作台位置（可访问）
// 3. Spawn 到地图上，标记为虚拟材料（CompVirtualIngredient）
// 4. 注入到 chosen 列表，让原版认为找到了材料

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
    /// 工作台制作材料查找：注入虚拟 Thing
    /// 核心逻辑：原版找不到材料时，从虚拟存储查找并创建虚拟 Thing
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

                // 创建临时 Thing（虚拟材料）
                Thing tempThing = ThingMaker.MakeThing(foundDef, foundStuff);
                tempThing.stackCount = neededCount;

                // 设置 position：如果使用预留物品，使用预留物品的 position；否则使用工作台的 InteractionCell
                IntVec3 workbenchPos;
                if (useReserved && foundReservedThing != null)
                {
                    workbenchPos = foundReservedThing.Position;
                }
                else
                {
                    // 使用工作台的 InteractionCell（如果存在），否则使用工作台位置
                    workbenchPos = GetBillGiverRootCell(billGiver, pawn);
                }
                tempThing.Position = workbenchPos;

                // 必须 Spawn 到地图上，否则 CanReserve 会失败
                GenSpawn.Spawn(tempThing, workbenchPos, pawn.Map, WipeMode.Vanish);

                // 检查 Pawn 是否可以访问虚拟 Thing
                if (!pawn.CanReserve(tempThing, 1, -1, null, false) || !pawn.CanReach(tempThing, PathEndMode.OnCell, Danger.Deadly))
                {
                    // 无法访问，销毁虚拟 Thing，跳过这个材料
                    if (tempThing.Spawned)
                    {
                        tempThing.DeSpawn(DestroyMode.Vanish);
                    }
                    tempThing.Destroy(DestroyMode.Vanish);
                    continue; // 跳过这个材料，继续查找下一个
                }

                // 标记为虚拟材料
                // 动态添加 CompVirtualIngredient（使用反射，避免修改共享的 ThingDef）
                CompVirtualIngredient comp = null;
                ThingWithComps thingWithComps = tempThing as ThingWithComps;
                if (thingWithComps != null)
                {
                    comp = thingWithComps.GetComp<CompVirtualIngredient>();
                }
                if (comp == null && thingWithComps != null)
                {
                    // 使用反射直接添加 Comp（参考 Vehicle Framework 的方法）
                    try
                    {
                        comp = new CompVirtualIngredient();
                        comp.parent = thingWithComps;
                        comp.Initialize(new CompProperties_VirtualIngredient());
                        
                        // 使用反射访问 comps 字段
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
                    comp.SetSourceCore(foundCore);
                    comp.SetSourceMap(foundCore.Map);
                    
                    // 设置预留物品信息
                    if (useReserved && foundReservedThing != null)
                    {
                        comp.SetFromReserved(true, foundReservedThing);
                    }
                    else if (foundReservedThing != null)
                    {
                        // 需要补足：标记预留物品和虚拟数量
                        comp.SetFromReserved(false, foundReservedThing);
                    }
                }

                virtualIngredients.Add(new ThingCount(tempThing, neededCount));
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
                    Log.Message($"[数字存储] 从虚拟存储注入 {virtualIngredients.Count} 种虚拟材料");
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
    }
}

