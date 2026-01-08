using DigitalStorage.Components;
using DigitalStorage.Data;
using DigitalStorage.Services;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 工作台计数兼容：让虚拟存储中的物品被计入"维持数量"
    /// </summary>
    [HarmonyPatch(typeof(RecipeWorkerCounter), "CountProducts")]
    public static class Patch_RecipeWorkerCounter_CountProducts
    {
        public static void Postfix(ref int __result, Bill_Production bill)
        {
            if (bill == null || bill.recipe == null || bill.recipe.products == null || bill.recipe.products.Count == 0)
            {
                return;
            }

            // 获取游戏组件
            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            // 获取要统计的物品定义
            ThingDefCountClass product = bill.recipe.products[0];
            ThingDef targetDef = product.thingDef;

            if (targetDef == null)
            {
                return;
            }

            // 统计虚拟存储中的物品数量
            int virtualCount = 0;

            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered)
                {
                    continue;
                }

                // 遍历核心中的所有物品
                foreach (var itemData in core.GetAllStoredItems())
                {
                    if (itemData.def != targetDef)
                    {
                        continue;
                    }

                    // 检查是否符合 Bill 的过滤条件
                    if (!IsValidForBill(itemData, bill, targetDef))
                    {
                        continue;
                    }

                    virtualCount += itemData.stackCount;
                }
            }

            // 将虚拟存储中的数量加入总数
            __result += virtualCount;
        }

        /// <summary>
        /// 检查虚拟存储中的物品是否符合 Bill 的过滤条件
        /// </summary>
        private static bool IsValidForBill(StoredItemData itemData, Bill_Production bill, ThingDef targetDef)
        {
            // 检查污染标记
            if (!bill.includeTainted && targetDef.IsApparel)
            {
                // 虚拟存储中的物品没有 WornByCorpse 信息，假设不污染
            }

            // 检查耐久度范围
            if (targetDef.useHitPoints && itemData.hitPoints > 0)
            {
                float hpPercent = (float)itemData.hitPoints / targetDef.BaseMaxHitPoints;
                if (!bill.hpRange.IncludesEpsilon(hpPercent))
                {
                    return false;
                }
            }

            // 检查品质范围
            if (itemData.quality != QualityCategory.Normal)
            {
                if (!bill.qualityRange.Includes(itemData.quality))
                {
                    return false;
                }
            }

            // 检查材质限制
            if (bill.limitToAllowedStuff && itemData.stuffDef != null)
            {
                if (!bill.ingredientFilter.Allows(itemData.stuffDef))
                {
                    return false;
                }
            }

            return true;
        }
    }
}

