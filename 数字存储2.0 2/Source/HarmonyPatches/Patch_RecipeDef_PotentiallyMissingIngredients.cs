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
            List<ThingDef> stillMissing = new List<ThingDef>();

            foreach (ThingDef missingDef in missingIngredients)
            {
                bool foundInStorage = false;

                // 遍历所有激活的核心
                foreach (Building_StorageCore core in gameComp.GetAllCores())
                {
                    if (core == null || !core.Spawned || !core.Powered)
                    {
                        continue;
                    }

                    // 检查核心中是否有该材料（检查所有可能的 stuff）
                    if (HasCompatibleItem(core, missingDef, __instance))
                    {
                        foundInStorage = true;
                        break;
                    }
                }

                // 如果虚拟存储中也没有，才算真正缺失
                if (!foundInStorage)
                {
                    stillMissing.Add(missingDef);
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

