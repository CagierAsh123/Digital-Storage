using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Components;
using DigitalStorage.Data;
using DigitalStorage.Services;
using DigitalStorage.Settings;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 交易系统兼容：让虚拟存储中的物品可以被交易
    /// 支持：轨道交易（商船）、商队交易（来访商人）
    /// </summary>
    
    /// <summary>
    /// 轨道交易信标检测：让有激活核心时也能进行轨道交易
    /// </summary>
    [HarmonyPatch(typeof(Building_OrbitalTradeBeacon), "AllPowered")]
    public static class Patch_OrbitalTradeBeacon_AllPowered
    {
        public static void Postfix(Map map, ref IEnumerable<Building_OrbitalTradeBeacon> __result)
        {
            if (map == null)
            {
                return;
            }

            // 检查是否有激活的核心
            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            bool hasActiveCore = false;
            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core != null && core.Spawned && core.Powered && core.Map == map)
                {
                    hasActiveCore = true;
                    break;
                }
            }

            if (!hasActiveCore)
            {
                return;
            }

            // 如果已经有信标，不需要处理
            if (__result != null && __result.Any())
            {
                return;
            }

            // 创建一个虚拟信标列表（使用核心作为"信标"）
            // 由于 AllPowered 返回的是 Building_OrbitalTradeBeacon 类型，
            // 我们需要找到地图上任何一个信标（即使没通电）来返回
            // 或者直接返回空列表，让后续的 AllLaunchableThingsForTrade 处理
            
            // 方案：查找地图上所有信标（包括没通电的），如果有就返回
            List<Building_OrbitalTradeBeacon> allBeacons = new List<Building_OrbitalTradeBeacon>();
            foreach (Building building in map.listerBuildings.allBuildingsColonist)
            {
                if (building is Building_OrbitalTradeBeacon beacon)
                {
                    allBeacons.Add(beacon);
                }
            }

            if (allBeacons.Count > 0)
            {
                // 有信标但没通电，如果有激活核心，返回这些信标（让交易可以进行）
                __result = allBeacons;
            }
            // 如果没有任何信标，无法绕过（需要至少一个信标建筑）
        }
    }

    /// <summary>
    /// 轨道交易物品列表：添加虚拟存储中的物品
    /// </summary>
    [HarmonyPatch(typeof(TradeUtility), "AllLaunchableThingsForTrade")]
    public static class Patch_TradeUtility_AllLaunchableThingsForTrade
    {
        public static void Postfix(Map map, ref IEnumerable<Thing> __result)
        {
            if (map == null)
            {
                return;
            }

            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            // 收集所有可交易的物品
            List<Thing> tradableThings = new List<Thing>(__result);

            // 添加虚拟存储中的物品
            TradeUtilityHelper.AddVirtualStorageItems(gameComp, map, tradableThings);

            __result = tradableThings;

            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] AllLaunchableThingsForTrade: 添加虚拟存储物品后共 {tradableThings.Count} 种");
            }
        }
    }

    /// <summary>
    /// 商队交易物品列表：添加虚拟存储中的物品
    /// </summary>
    [HarmonyPatch(typeof(Pawn_TraderTracker), "ColonyThingsWillingToBuy")]
    public static class Patch_Pawn_TraderTracker_ColonyThingsWillingToBuy
    {
        public static void Postfix(Pawn playerNegotiator, Pawn ___pawn, ref IEnumerable<Thing> __result)
        {
            if (___pawn == null || ___pawn.Map == null)
            {
                return;
            }

            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            // 收集所有可交易的物品
            List<Thing> tradableThings = new List<Thing>(__result);

            // 添加虚拟存储中的物品
            TradeUtilityHelper.AddVirtualStorageItems(gameComp, ___pawn.Map, tradableThings);

            __result = tradableThings;

            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] ColonyThingsWillingToBuy: 添加虚拟存储物品后共 {tradableThings.Count} 种");
            }
        }
    }

    /// <summary>
    /// 交易物品位置检查：让虚拟存储物品通过检查
    /// </summary>
    [HarmonyPatch(typeof(TradeDeal), "InSellablePosition")]
    public static class Patch_TradeDeal_InSellablePosition
    {
        public static bool Prefix(Thing t, ref string reason, ref bool __result)
        {
            if (t == null)
            {
                return true;
            }

            // 如果是虚拟存储物品，直接返回 true
            if (TradeItemTracker.GetTradeItemInfo(t) != null)
            {
                reason = null;
                __result = true;
                return false; // 跳过原版检查
            }

            return true;
        }
    }

    /// <summary>
    /// 辅助方法：添加虚拟存储中的物品到交易列表
    /// </summary>
    public static class TradeUtilityHelper
    {
        public static void AddVirtualStorageItems(DigitalStorageGameComponent gameComp, Map map, List<Thing> tradableThings)
        {
            if (gameComp == null || map == null)
            {
                return;
            }

            // 遍历所有激活的核心
            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered || core.Map != map)
                {
                    continue;
                }

                // 从虚拟存储生成真实物品用于交易
                foreach (StoredItemData itemData in core.GetAllStoredItems())
                {
                    if (itemData == null || itemData.def == null || itemData.stackCount <= 0)
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

        public static void RegisterTradeItem(Thing tradeThing, Building_StorageCore core, StoredItemData itemData)
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
            if (tradeThing == null)
            {
                return null;
            }
            TradeItemInfo info;
            _tradeItems.TryGetValue(tradeThing, out info);
            return info;
        }

        public static void UnregisterTradeItem(Thing tradeThing)
        {
            if (tradeThing != null)
            {
                _tradeItems.Remove(tradeThing);
            }
        }

        public static void Clear()
        {
            _tradeItems.Clear();
        }
    }
}
