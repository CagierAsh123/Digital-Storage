// 远行队虚拟存储访问
// 让有芯片成员的远行队可以远程访问基地的虚拟存储

using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Data;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DigitalStorage.Services
{
    public static class CaravanStorageAccess
    {
        // 缓存：避免重复创建Thing
        private static Dictionary<Caravan, List<Thing>> virtualThingsCache = new Dictionary<Caravan, List<Thing>>();
        private static int lastCacheTick = -1;

        /// <summary>
        /// 检查远行队是否有芯片成员
        /// </summary>
        public static bool CaravanHasTerminalImplant(Caravan caravan)
        {
            if (caravan == null) return false;
            
            foreach (Pawn pawn in caravan.PawnsListForReading)
            {
                if (PawnStorageAccess.HasTerminalImplant(pawn))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 获取远行队可访问的所有虚拟物品（作为Thing列表）
        /// </summary>
        public static List<Thing> GetVirtualThingsForCaravan(Caravan caravan)
        {
            // 每tick只计算一次
            int currentTick = Find.TickManager.TicksGame;
            if (lastCacheTick == currentTick && virtualThingsCache.TryGetValue(caravan, out var cached))
            {
                return cached;
            }

            var result = new List<Thing>();
            var gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null) return result;

            foreach (var core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered) continue;

                foreach (var itemData in core.GetAllStoredItems())
                {
                    if (itemData.def == null || itemData.stackCount <= 0) continue;
                    
                    // 创建临时Thing用于显示（不Spawn到地图）
                    Thing virtualThing = itemData.CreateThing();
                    if (virtualThing != null)
                    {
                        result.Add(virtualThing);
                    }
                }
            }

            // 更新缓存
            virtualThingsCache[caravan] = result;
            lastCacheTick = currentTick;

            return result;
        }

        /// <summary>
        /// 从基地虚拟存储提取物品到远行队
        /// </summary>
        public static Thing ExtractItemForCaravan(Caravan caravan, ThingDef def, int count, ThingDef stuff = null)
        {
            var gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null) return null;

            // 从任意核心提取
            Thing extracted = gameComp.TryExtractItemFromAnyCoreGlobal(def, count, stuff, null);
            if (extracted == null) return null;

            // 放入远行队成员背包
            Pawn carrier = CaravanInventoryUtility.FindPawnToMoveInventoryTo(extracted, caravan.PawnsListForReading, null, null);
            if (carrier != null)
            {
                carrier.inventory.TryAddAndUnforbid(extracted);
            }

            // 清除缓存
            virtualThingsCache.Remove(caravan);

            return extracted;
        }

        /// <summary>
        /// 检查虚拟存储中是否有指定物品
        /// </summary>
        public static bool HasVirtualItem(ThingDef def, ThingDef stuff = null)
        {
            var gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null) return false;

            foreach (var core in gameComp.GetAllCores())
            {
                if (core != null && core.Spawned && core.Powered && core.HasItem(def, stuff))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 获取虚拟存储中指定物品的总数量
        /// </summary>
        public static int GetVirtualItemCount(ThingDef def, ThingDef stuff = null)
        {
            var gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null) return 0;

            int total = 0;
            foreach (var core in gameComp.GetAllCores())
            {
                if (core != null && core.Spawned && core.Powered)
                {
                    total += core.GetItemCount(def, stuff);
                }
            }
            return total;
        }

        /// <summary>
        /// 清除缓存（地图变化时调用）
        /// </summary>
        public static void ClearCache()
        {
            virtualThingsCache.Clear();
            lastCacheTick = -1;
        }
    }
}
