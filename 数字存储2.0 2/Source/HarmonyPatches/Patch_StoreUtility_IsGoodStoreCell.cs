using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 补丁：允许核心建筑的存储单元格通过路径检查
    /// 即使核心建筑设置了 PassThroughOnly，原版的路径检查可能仍然失败
    /// </summary>
    [HarmonyPatch(typeof(StoreUtility), "IsGoodStoreCell")]
    public static class Patch_StoreUtility_IsGoodStoreCell
    {
        public static void Postfix(IntVec3 c, Map map, Thing t, Pawn carrier, Faction faction, ref bool __result)
        {
            // 如果原版已经判断是好的存储单元格，不需要处理
            if (__result)
            {
                return;
            }

            // 如果没有搬运者，不需要处理
            if (carrier == null)
            {
                return;
            }

            // 检查该单元格是否是核心建筑的存储单元格
            SlotGroup slotGroup = c.GetSlotGroup(map);
            if (slotGroup == null || slotGroup.parent == null)
            {
                return;
            }

            Building_StorageCore core = slotGroup.parent as Building_StorageCore;
            if (core == null || !core.Spawned || !core.Powered)
            {
                return;
            }

            // 检查核心是否接受该物品
            if (!core.Accepts(t))
            {
                return;
            }

            // 核心建筑的单元格应该可以通过路径检查（因为设置了 PassThroughOnly）
            // 如果原版判断不可到达，可能是因为核心建筑被当作障碍物
            // 我们允许这种情况，因为核心建筑是可通行的
            Thing spawnedParentOrMe = t.SpawnedParentOrMe;
            IntVec3 startPos;
            if (spawnedParentOrMe != null)
            {
                if (spawnedParentOrMe != t && spawnedParentOrMe.def.hasInteractionCell)
                {
                    startPos = spawnedParentOrMe.InteractionCell;
                }
                else
                {
                    startPos = spawnedParentOrMe.Position;
                }
            }
            else
            {
                startPos = carrier.PositionHeld;
            }

            // 如果原版判断不可到达，但这是核心建筑的单元格，我们允许它
            // 因为核心建筑设置了 PassThroughOnly，Pawn 应该可以到达
            // 但是，我们需要确保至少可以到达核心建筑的某个相邻单元格
            // 这样 Pawn 就可以走到核心的单元格（因为核心是可通行的）
            foreach (IntVec3 adjCell in GenAdj.CellsAdjacent8Way(core))
            {
                if (adjCell.InBounds(map) && 
                    carrier.Map.reachability.CanReach(startPos, adjCell, PathEndMode.Touch, 
                        TraverseParms.For(carrier, Danger.Deadly, TraverseMode.ByPawn, false, false, false, true)))
                {
                    // 可以到达核心的相邻单元格，因此可以到达核心的单元格（因为核心是可通行的）
                    __result = true;
                    return;
                }
            }
        }
    }
}

