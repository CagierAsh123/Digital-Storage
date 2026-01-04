// 工作台制作材料查找：补货+传送逻辑
// 核心功能：
// 1. 有芯片：补货 + 传送到工作台位置
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

            // 检查是否有芯片（决定是否传送）
            bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);

            Bill_Production billProd = bill as Bill_Production;
            if (billProd == null)
            {
                return;
            }

            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] TryFindBestBillIngredients: 配方={billProd.recipe.defName}, hasChip={hasChip}");
            }

            List<ThingCount> virtualIngredients = new List<ThingCount>();

            foreach (IngredientCount ingredient in billProd.recipe.ingredients)
            {
                ThingDef anyDef = ingredient.filter.AnyAllowedDef;
                if (anyDef == null)
                {
                    return;
                }

                int neededCount = ingredient.CountRequiredOfFor(anyDef, billProd.recipe, billProd);

                // 查找材料
                var result = FindMaterialInCores(gameComp, ingredient, billProd, neededCount);
                if (result.core == null || result.def == null)
                {
                    return; // 找不到材料
                }

                // 确定补货位置
                IntVec3 replenishPos = result.reservedThing?.Position ?? result.core.Position;

                // 限制补货数量
                int maxCanCarry = pawn.carryTracker.MaxStackSpaceEver(result.def);
                int actualReplenishCount = Math.Min(neededCount, maxCanCarry);

                // 补货
                Thing replenishedThing = ReplenishToNeededCount(result.core, result.def, result.stuff, actualReplenishCount, replenishPos, pawn.Map, result.reservedThing);

                if (replenishedThing == null || !replenishedThing.Spawned)
                {
                    return;
                }

                // 有芯片则传送到工作台位置
                if (hasChip)
                {
                    IntVec3 workbenchPos = GetBillGiverRootCell(billGiver, pawn);
                    if (workbenchPos != replenishedThing.Position)
                    {
                        replenishedThing = TeleportItem(replenishedThing, workbenchPos, pawn.Map);
                        if (replenishedThing == null)
                        {
                            return;
                        }
                    }
                }

                // 检查可访问性
                if (!pawn.CanReserve(replenishedThing, 1, -1, null, false) || !pawn.CanReach(replenishedThing, PathEndMode.OnCell, Danger.Deadly))
                {
                    return;
                }

                virtualIngredients.Add(new ThingCount(replenishedThing, actualReplenishCount));
            }

            // 找到所有材料
            if (virtualIngredients.Count == billProd.recipe.ingredients.Count)
            {
                if (chosen == null)
                {
                    chosen = new List<ThingCount>();
                }
                chosen.AddRange(virtualIngredients);
                __result = true;

                if (DigitalStorageSettings.enableDebugLog)
                {
                    Log.Message($"[数字存储] TryFindBestBillIngredients: 成功找到 {virtualIngredients.Count} 种材料");
                }
            }
        }

        private static (Building_StorageCore core, ThingDef def, ThingDef stuff, Thing reservedThing) FindMaterialInCores(
            DigitalStorageGameComponent gameComp, IngredientCount ingredient, Bill_Production billProd, int neededCount)
        {
            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered)
                {
                    continue;
                }

                // 先检查预留物品
                ThingDef anyDef = ingredient.filter.AnyAllowedDef;
                Thing reservedThing = core.FindReservedItem(anyDef, null);
                
                if (reservedThing != null && reservedThing.Spawned)
                {
                    if (!ingredient.filter.Allows(reservedThing.def))
                    {
                        continue;
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
                            return (core, reservedThing.def, reservedThing.Stuff, reservedThing);
                        }
                    }
                }

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
                        return (core, itemData.def, itemData.stuffDef, null);
                    }
                }
            }

            return (null, null, null, null);
        }

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
            return forPawn.Position;
        }

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
                return existingReserved;
            }

            Thing extracted = core.ExtractItem(def, needMore, stuff);
            if (extracted == null)
            {
                return existingReserved;
            }

            if (existingReserved != null && existingReserved.Spawned)
            {
                bool absorbed = existingReserved.TryAbsorbStack(extracted, false);
                if (absorbed)
                {
                    try
                    {
                        if (extracted != null && !extracted.Destroyed && extracted.stackCount > 0)
                        {
                            GenSpawn.Spawn(extracted, existingReserved.Position, map, WipeMode.Vanish);
                        }
                    }
                    catch { }
                    return existingReserved;
                }
                else
                {
                    GenSpawn.Spawn(extracted, existingReserved.Position, map, WipeMode.Vanish);
                    return existingReserved;
                }
            }
            else
            {
                GenSpawn.Spawn(extracted, position, map, WipeMode.Vanish);
                return extracted;
            }
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
    }
}
