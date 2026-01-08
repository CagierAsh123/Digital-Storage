// 远行队虚拟存储支持
// 让有芯片成员的远行队可以：
// 1. 在交易时使用虚拟存储的物品
// 2. 在需要时从虚拟存储获取食物/药物

using System.Collections.Generic;
using DigitalStorage.Services;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// Patch Settlement_TraderTracker.ColonyThingsWillingToBuy
    /// 让远行队和据点交易时可以卖出虚拟存储的物品
    /// </summary>
    [HarmonyPatch(typeof(Settlement_TraderTracker), nameof(Settlement_TraderTracker.ColonyThingsWillingToBuy))]
    public static class Patch_Settlement_TraderTracker_ColonyThingsWillingToBuy
    {
        public static IEnumerable<Thing> Postfix(IEnumerable<Thing> __result, Settlement_TraderTracker __instance, Pawn playerNegotiator)
        {
            // 先返回原版结果
            foreach (var thing in __result)
            {
                yield return thing;
            }

            // 检查远行队是否有芯片成员
            Caravan caravan = playerNegotiator?.GetCaravan();
            if (caravan == null || !CaravanStorageAccess.CaravanHasTerminalImplant(caravan))
                yield break;

            // 注入虚拟存储物品
            foreach (var virtualThing in CaravanStorageAccess.GetVirtualThingsForCaravan(caravan))
            {
                yield return virtualThing;
            }
        }
    }

    /// <summary>
    /// Patch CaravanInventoryUtility.TryGetBestFood
    /// 让远行队成员可以从虚拟存储获取食物
    /// </summary>
    [HarmonyPatch(typeof(CaravanInventoryUtility), nameof(CaravanInventoryUtility.TryGetBestFood))]
    public static class Patch_CaravanInventoryUtility_TryGetBestFood
    {
        public static void Postfix(Caravan caravan, Pawn forPawn, ref Thing food, ref Pawn owner, ref bool __result)
        {
            // 如果已经找到食物，不需要处理
            if (__result) return;

            // 检查远行队是否有芯片成员
            if (caravan == null || !CaravanStorageAccess.CaravanHasTerminalImplant(caravan))
                return;

            // 从虚拟存储查找最佳食物
            Thing bestFood = null;
            float bestScore = 0f;

            foreach (var virtualThing in CaravanStorageAccess.GetVirtualThingsForCaravan(caravan))
            {
                if (CaravanPawnsNeedsUtility.CanEatForNutritionNow(virtualThing, forPawn))
                {
                    float score = CaravanPawnsNeedsUtility.GetFoodScore(virtualThing, forPawn);
                    if (bestFood == null || score > bestScore)
                    {
                        bestFood = virtualThing;
                        bestScore = score;
                    }
                }
            }

            if (bestFood != null)
            {
                // 从虚拟存储提取食物到远行队
                Thing extracted = CaravanStorageAccess.ExtractItemForCaravan(caravan, bestFood.def, 1, bestFood.Stuff);
                if (extracted != null)
                {
                    food = extracted;
                    owner = CaravanInventoryUtility.GetOwnerOf(caravan, extracted);
                    __result = true;
                }
            }
        }
    }

    /// <summary>
    /// Patch CaravanInventoryUtility.TryGetBestMedicine
    /// 让远行队成员可以从虚拟存储获取药物
    /// </summary>
    [HarmonyPatch(typeof(CaravanInventoryUtility), nameof(CaravanInventoryUtility.TryGetBestMedicine))]
    public static class Patch_CaravanInventoryUtility_TryGetBestMedicine
    {
        public static void Postfix(Caravan caravan, Pawn patient, ref Thing medicine, ref Pawn owner, ref bool __result)
        {
            // 如果已经找到药物，不需要处理
            if (__result) return;

            // 检查远行队是否有芯片成员
            if (caravan == null || !CaravanStorageAccess.CaravanHasTerminalImplant(caravan))
                return;

            // 从虚拟存储查找最佳药物
            Thing bestMedicine = null;
            float bestPotency = 0f;

            foreach (var virtualThing in CaravanStorageAccess.GetVirtualThingsForCaravan(caravan))
            {
                if (virtualThing.def.IsMedicine)
                {
                    float potency = virtualThing.GetStatValue(StatDefOf.MedicalPotency, true);
                    if (bestMedicine == null || potency > bestPotency)
                    {
                        bestMedicine = virtualThing;
                        bestPotency = potency;
                    }
                }
            }

            if (bestMedicine != null)
            {
                // 从虚拟存储提取药物到远行队
                Thing extracted = CaravanStorageAccess.ExtractItemForCaravan(caravan, bestMedicine.def, 1, bestMedicine.Stuff);
                if (extracted != null)
                {
                    medicine = extracted;
                    owner = CaravanInventoryUtility.GetOwnerOf(caravan, extracted);
                    __result = true;
                }
            }
        }
    }
}
