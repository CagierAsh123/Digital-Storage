using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Components;
using DigitalStorage.Services;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 交易系统兼容：让虚拟存储中的物品可以被交易
    /// </summary>
    [HarmonyPatch(typeof(TradeUtility), "AllLaunchableThingsForTrade")]
    public static class Patch_TradeUtility
    {
        public static void Postfix(Map map, ref IEnumerable<Thing> __result)
        {
            if (map == null)
            {
                return;
            }

            // 获取游戏组件
            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            // 收集所有可交易的物品
            HashSet<Thing> tradableThings = new HashSet<Thing>(__result);

            // 遍历所有激活的核心
            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered || core.Map != map)
                {
                    continue;
                }

                // 从虚拟存储生成真实物品用于交易
                foreach (var itemData in core.GetAllStoredItems())
                {
                    if (itemData == null || itemData.def == null)
                    {
                        continue;
                    }

                    // 创建临时 Thing 对象（不 Spawn）
                    Thing tradeThing = ThingMaker.MakeThing(itemData.def, itemData.stuffDef);
                    tradeThing.stackCount = itemData.stackCount;
                    
                    // 设置品质
                    CompQuality qualityComp = tradeThing.TryGetComp<CompQuality>();
                    if (itemData.quality != QualityCategory.Awful && qualityComp != null)
                    {
                        qualityComp.SetQuality(itemData.quality, ArtGenerationContext.Colony);
                    }
                    
                    // 设置耐久
                    if (itemData.hitPoints > 0 && tradeThing.def.useHitPoints)
                    {
                        tradeThing.HitPoints = itemData.hitPoints;
                    }

                    // 标记来源（用于交易后从虚拟存储扣除）
                    TradeItemTracker.RegisterTradeItem(tradeThing, core, itemData);

                    tradableThings.Add(tradeThing);
                }
            }

            __result = tradableThings;
        }
    }

    /// <summary>
    /// 交易物品追踪器：记录交易物品与虚拟存储的对应关系
    /// </summary>
    public static class TradeItemTracker
    {
        private static readonly Dictionary<Thing, TradeItemInfo> _tradeItems = new Dictionary<Thing, TradeItemInfo>();

        public class TradeItemInfo
        {
            public Building_StorageCore sourceCore;
            public ThingDef def;
            public ThingDef stuffDef;
            public int stackCount;
            public QualityCategory quality;
            public int hitPoints;
        }

        public static void RegisterTradeItem(Thing tradeThing, Building_StorageCore core, Data.StoredItemData itemData)
        {
            _tradeItems[tradeThing] = new TradeItemInfo
            {
                sourceCore = core,
                def = itemData.def,
                stuffDef = itemData.stuffDef,
                stackCount = itemData.stackCount,
                quality = itemData.quality,
                hitPoints = itemData.hitPoints
            };
        }

        public static TradeItemInfo GetTradeItemInfo(Thing tradeThing)
        {
            TradeItemInfo info;
            _tradeItems.TryGetValue(tradeThing, out info);
            return info;
        }

        public static void UnregisterTradeItem(Thing tradeThing)
        {
            _tradeItems.Remove(tradeThing);
        }

        public static void Clear()
        {
            _tradeItems.Clear();
        }
    }
}

