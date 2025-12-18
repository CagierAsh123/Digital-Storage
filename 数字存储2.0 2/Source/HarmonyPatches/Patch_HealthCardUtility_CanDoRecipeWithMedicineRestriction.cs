using DigitalStorage.Components;
using DigitalStorage.Services;
using HarmonyLib;
using RimWorld;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// Hook HealthCardUtility.CanDoRecipeWithMedicineRestriction
    /// 让手术清单 UI 能检测到虚拟存储中的药物
    /// </summary>
    [HarmonyPatch(typeof(HealthCardUtility), "CanDoRecipeWithMedicineRestriction")]
    public static class Patch_HealthCardUtility_CanDoRecipeWithMedicineRestriction
    {
        public static void Postfix(ref bool __result, IBillGiver giver, RecipeDef recipe)
        {
            // 如果已经找到药物，不需要处理
            if (__result)
            {
                return;
            }

            // 获取 Pawn
            Pawn pawn = giver as Pawn;
            if (pawn == null || pawn.playerSettings == null)
            {
                return;
            }

            // 检查配方是否需要药物
            if (!recipe.ingredients.Any(x => x.filter.AnyAllowedDef.IsMedicine))
            {
                return;
            }

            // 检查 Pawn 是否有访问权限
            bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
            bool hasInterface = false;

            if (!hasChip && pawn.MapHeld != null)
            {
                DigitalStorageMapComponent mapComp = pawn.MapHeld.GetComponent<DigitalStorageMapComponent>();
                if (mapComp != null)
                {
                    foreach (Building_OutputInterface iface in mapComp.GetAllOutputInterfaces())
                    {
                        if (iface != null && iface.Spawned && iface.IsActive && iface.BoundCore != null && iface.BoundCore.Powered)
                        {
                            hasInterface = true;
                            break;
                        }
                    }
                }
            }

            if (!hasChip && !hasInterface)
            {
                return;
            }

            // 获取医疗护理等级
            MedicalCareCategory medicalCareCategory = WorkGiver_DoBill.GetMedicalCareCategory(pawn);

            // 检查虚拟存储中是否有符合条件的药物
            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered)
                {
                    continue;
                }

                foreach (var itemData in core.GetAllStoredItems())
                {
                    if (itemData?.def == null || !itemData.def.IsMedicine)
                    {
                        continue;
                    }

                    // 检查是否符合配方要求
                    foreach (IngredientCount ingredient in recipe.ingredients)
                    {
                        if (ingredient.filter.Allows(itemData.def) && medicalCareCategory.AllowsMedicine(itemData.def))
                        {
                            __result = true;
                            return;
                        }
                    }
                }
            }
        }
    }
}

