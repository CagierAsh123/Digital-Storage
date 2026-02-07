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

            if (Mathf.Approximately(multiplier, 1.0f))
            {
                return;
            }

            List<string> defNames = new List<string>
            {
                "DigitalStorage_StorageCore",
                "DigitalStorage_InputInterface",
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
                        if (newCount < 1)
                        {
                            newCount = 1;
                        }
                        newCostList.Add(new ThingDefCountClass(cost.thingDef, newCount));
                    }
                    thingDef.costList = newCostList;
                }
            }

            List<string> recipeDefNames = new List<string>
            {
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
