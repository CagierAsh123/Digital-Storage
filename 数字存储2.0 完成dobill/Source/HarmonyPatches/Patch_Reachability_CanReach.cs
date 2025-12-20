// TODO: 待部分修改 - 新架构
// 需要简化的部分：
// 1. 简化逻辑，只处理虚拟物品访问检查
// 2. 预留物品（真实物品）应该可以正常到达，不需要特殊处理
// 3. 虚拟物品需要芯片或输出接口才能访问

using System;
using DigitalStorage.Components;
using DigitalStorage.Services;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    [HarmonyPatch(typeof(Reachability), "CanReach", new Type[]
    {
        typeof(IntVec3),
        typeof(LocalTargetInfo),
        typeof(PathEndMode),
        typeof(TraverseParms)
    })]
    public static class Patch_Reachability_CanReach
    {
        public static void Postfix(IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms traverseParams, ref bool __result, Map ___map)
        {
            // 只处理物品目标
            if (!dest.HasThing || dest.Thing.def.category != ThingCategory.Item)
            {
                return;
            }

            Thing thing = dest.Thing;
            
            // 跳过尸体和其他不应该存储的物品
            if (thing is Corpse)
            {
                return;
            }
            
            // 只处理已生成的物品
            if (!thing.Spawned)
            {
                return;
            }

            // 检查是否是虚拟材料（CompVirtualIngredient）
            CompVirtualIngredient virtualComp = null;
            if (thing is ThingWithComps thingWithComps)
            {
                virtualComp = thingWithComps.GetComp<CompVirtualIngredient>();
            }
            if (virtualComp == null || !virtualComp.IsVirtual)
            {
                // 不是虚拟材料，可能是预留物品或其他来源，不需要特殊处理
                // 预留物品是真实物品，核心建筑已设置为 PassThroughOnly，应该可以正常到达
                return;
            }

            // 只处理虚拟物品访问检查
            Pawn pawn = traverseParams.pawn;
            if (pawn == null)
            {
                return;
            }

            // 虚拟物品需要芯片或输出接口才能访问
            if (!PawnStorageAccess.HasTerminalImplant(pawn))
            {
                // 没有芯片，检查是否有输出接口可以到达
                DigitalStorageMapComponent mapComp = ___map?.GetComponent<DigitalStorageMapComponent>();
                if (mapComp != null)
                {
                    bool canReachInterface = false;
                    foreach (Building_OutputInterface iface in mapComp.GetAllOutputInterfaces())
                    {
                        if (iface != null && iface.Spawned && iface.IsActive)
                        {
                            if (___map.reachability.CanReach(start, iface.Position, PathEndMode.Touch, traverseParams))
                            {
                                canReachInterface = true;
                                break;
                            }
                        }
                    }
                    
                    if (!canReachInterface)
                    {
                        // 没有芯片且无法到达输出接口，拒绝访问虚拟物品
                        __result = false;
                        return;
                    }
                }
                else
                {
                    // 没有 MapComponent，拒绝访问
                    __result = false;
                    return;
                }
            }

            // 有芯片或可以到达输出接口，允许访问虚拟物品
            __result = true;
        }
    }
}

