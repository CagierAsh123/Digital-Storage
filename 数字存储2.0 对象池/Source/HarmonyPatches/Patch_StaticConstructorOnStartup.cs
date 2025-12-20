using System;
using System.Collections.Generic;
using DigitalStorage.Settings;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [HarmonyPatch(typeof(StaticConstructorOnStartupUtility), "CallAll")]
    public static class Patch_StaticConstructorOnStartup
    {
        public static void Postfix()
        {
            ApplyCostMultiplier();
        }

        private static void ApplyCostMultiplier()
        {
            float multiplier = DigitalStorageSettings.costMultiplier;
            
            if (multiplier == 1.0f)
            {
                return; // 默认值，不需要修改
            }

            // 需要修改的 defName 列表
            List<string> defNames = new List<string>
            {
                // 建筑
                "DigitalStorage_StorageCore",
                "DigitalStorage_DiskCabinet",
                "DigitalStorage_Interface",
                // 磁盘
                "DigitalStorage_DiskSmall",
                "DigitalStorage_DiskMedium",
                "DigitalStorage_DiskLarge",
                // 终端芯片
                "DigitalStorage_TerminalChip"
            };

            foreach (string defName in defNames)
            {
                ThingDef thingDef = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
                if (thingDef != null && thingDef.costList != null)
                {
                    List<ThingDefCountClass> newCostList = new List<ThingDefCountClass>();
                    foreach (ThingDefCountClass cost in thingDef.costList)
                    {
                        int newCount = Mathf.RoundToInt(cost.count * multiplier);
                        // 确保至少为 1
                        if (newCount < 1)
                        {
                            newCount = 1;
                        }
                        newCostList.Add(new ThingDefCountClass(cost.thingDef, newCount));
                    }
                    thingDef.costList = newCostList;
                }
            }

            // 同时修改配方的材料消耗
            List<string> recipeDefNames = new List<string>
            {
                "Make_DigitalStorage_DiskSmall",
                "Make_DigitalStorage_DiskMedium",
                "Make_DigitalStorage_DiskLarge",
                "Make_DigitalStorage_TerminalChip"
            };

            foreach (string recipeName in recipeDefNames)
            {
                RecipeDef recipeDef = DefDatabase<RecipeDef>.GetNamedSilentFail(recipeName);
                if (recipeDef != null && recipeDef.ingredients != null)
                {
                    foreach (IngredientCount ingredient in recipeDef.ingredients)
                    {
                        ingredient.SetBaseCount(ingredient.GetBaseCount() * multiplier);
                    }
                }
            }
        }
    }
}

