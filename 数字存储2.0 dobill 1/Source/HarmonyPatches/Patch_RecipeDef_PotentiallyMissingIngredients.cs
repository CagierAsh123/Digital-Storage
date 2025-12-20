using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Components;
using DigitalStorage.Services;
using HarmonyLib;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 手术材料检测兼容：让虚拟存储中的材料被识别
    /// </summary>
    [HarmonyPatch(typeof(RecipeDef), "PotentiallyMissingIngredients")]
    public static class Patch_RecipeDef_PotentiallyMissingIngredients
    {
        public static void Postfix(ref IEnumerable<ThingDef> __result, RecipeDef __instance, Pawn billDoer, Map map)
        {
            if (map == null || __instance == null)
            {
                return;
            }

            // 获取游戏组件
            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            // 检查是否有可用的虚拟存储访问方式
            bool hasAccess = false;
            
            // 检查 billDoer 是否有芯片
            if (billDoer != null && PawnStorageAccess.HasTerminalImplant(billDoer))
            {
                hasAccess = true;
            }
            
            // 检查是否有激活的输出接口
            if (!hasAccess)
            {
                DigitalStorageMapComponent mapComp = map.GetComponent<DigitalStorageMapComponent>();
                if (mapComp != null)
                {
                    foreach (Building_OutputInterface iface in mapComp.GetAllOutputInterfaces())
                    {
                        if (iface != null && iface.Spawned && iface.IsActive && iface.BoundCore != null && iface.BoundCore.Powered)
                        {
                            hasAccess = true;
                            break;
                        }
                    }
                }
            }

            if (!hasAccess)
            {
                // 没有访问虚拟存储的方式，不处理
                return;
            }

            // 获取原始的缺失材料列表
            List<ThingDef> missingIngredients = __result.ToList();
            
            if (missingIngredients.Count == 0)
            {
                // 没有缺失材料，不需要处理
                return;
            }

            // 检查虚拟存储中是否有这些材料
            // 注意：我们需要检查每个ingredient，而不是每个missingDef
            // 因为一个ingredient可能对应多个材料类型（如：玻璃钢、白银、钢铁）
            // 只要虚拟存储中有任何一个材料类型有足够的数量，就认为该ingredient不缺失
            List<ThingDef> stillMissing = new List<ThingDef>();

            // 遍历所有ingredients，检查是否有足够的材料
            foreach (IngredientCount ingredient in __instance.ingredients)
            {
                bool foundInStorage = false;
                
                // 遍历所有可能的材料类型（检查ingredient.filter允许的所有材料）
                foreach (ThingDef allowedDef in ingredient.filter.AllowedThingDefs)
                {
                    // 检查是否符合recipe.fixedIngredientFilter
                    if (!ingredient.IsFixedIngredient && __instance.fixedIngredientFilter != null && !__instance.fixedIngredientFilter.Allows(allowedDef))
                    {
                        continue;
                    }
                    
                    // 计算该材料类型需要的数量（使用CountRequiredOfFor，考虑recipe的IngredientValueGetter）
                    // 不同材料类型的ValuePerUnitOf可能不同，所以需要分别计算
                    int neededCount = ingredient.CountRequiredOfFor(allowedDef, __instance, null);
                    
                    // 检查虚拟存储中是否有足够的数量
                    int totalAvailable = 0;
                    foreach (Building_StorageCore core in gameComp.GetAllCores())
                    {
                        if (core == null || !core.Spawned || !core.Powered)
                        {
                            continue;
                        }
                        
                        // 检查数量是否足够
                        int availableCount = core.GetItemCount(allowedDef, null);
                        totalAvailable += availableCount;
                        
                        if (totalAvailable >= neededCount)
                        {
                            foundInStorage = true;
                            break;
                        }
                    }
                    
                    if (foundInStorage)
                    {
                        break;
                    }
                }
                
                // 如果虚拟存储中没有足够的材料，将该ingredient对应的缺失材料添加到列表
                if (!foundInStorage)
                {
                    // 找到该ingredient对应的缺失材料（从missingIngredients中查找）
                    ThingDef missingDef = missingIngredients.FirstOrDefault(def => ingredient.filter.Allows(def));
                    if (missingDef != null && !stillMissing.Contains(missingDef))
                    {
                        stillMissing.Add(missingDef);
                    }
                }
            }

            // 返回真正缺失的材料列表
            __result = stillMissing;
        }

        /// <summary>
        /// 检查核心中是否有兼容的物品
        /// </summary>
        private static bool HasCompatibleItem(Building_StorageCore core, ThingDef def, RecipeDef recipe)
        {
            // 对于手术材料（如草药），不需要检查 stuff
            if (def.MadeFromStuff)
            {
                // 检查所有可能的 stuff
                foreach (var itemData in core.GetAllStoredItems())
                {
                    if (itemData.def == def)
                    {
                        // 检查是否符合配方的过滤器
                        if (recipe.fixedIngredientFilter == null || recipe.fixedIngredientFilter.Allows(def))
                        {
                            return true;
                        }
                    }
                }
                return false;
            }
            else
            {
                // 不需要 stuff，直接检查
                return core.HasItem(def, null);
            }
        }
    }
}

