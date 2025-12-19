using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Services;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 交易行为兼容：交易完成后从虚拟存储扣除物品
    /// </summary>
    [HarmonyPatch(typeof(Pawn_TraderTracker), "GiveSoldThingToTrader")]
    public static class Patch_TradeAction_GiveSoldThingToTrader
    {
        public static bool Prefix(Thing toGive, int countToGive, Pawn playerNegotiator)
        {
            if (toGive == null)
            {
                return true;
            }

            // 检查是否是虚拟存储中的物品
            var tradeInfo = TradeItemTracker.GetTradeItemInfo(toGive);
            if (tradeInfo == null)
            {
                // 不是虚拟存储物品，使用原版逻辑
                return true;
            }

            // 从虚拟存储扣除物品
            if (tradeInfo.sourceCore != null && tradeInfo.sourceCore.Spawned && tradeInfo.sourceCore.Powered)
            {
                // 扣除指定数量
                Thing extracted = tradeInfo.sourceCore.ExtractItem(tradeInfo.def, countToGive, tradeInfo.stuffDef);
                
                if (extracted != null)
                {
                    // 销毁提取的物品（已经交易给商人）
                    extracted.Destroy(DestroyMode.Vanish);
                }
            }

            // 清理追踪
            TradeItemTracker.UnregisterTradeItem(toGive);

            // 阻止原版逻辑（因为物品不在地图上）
            return false;
        }
    }

    /// <summary>
    /// 交易对话框关闭时清理追踪
    /// </summary>
    [HarmonyPatch(typeof(Dialog_Trade), "Close")]
    public static class Patch_Dialog_Trade_Close
    {
        public static void Postfix()
        {
            // 清理所有交易物品追踪
            TradeItemTracker.Clear();
        }
    }

    /// <summary>
    /// 轨道交易信标兼容：让虚拟存储核心也能作为交易信标
    /// </summary>
    [HarmonyPatch(typeof(Building_OrbitalTradeBeacon), "TradeableCellsAround")]
    public static class Patch_OrbitalTradeBeacon_TradeableCells
    {
        public static void Postfix(IntVec3 pos, Map map, ref IEnumerable<IntVec3> __result)
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

            // 检查是否有激活的核心
            bool hasActiveCore = false;
            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core != null && core.Spawned && core.Powered && core.Map == map)
                {
                    hasActiveCore = true;
                    break;
                }
            }

            if (hasActiveCore)
            {
                // 如果有激活的核心，扩展可交易区域
                var tradableCells = new System.Collections.Generic.HashSet<IntVec3>(__result);
                
                foreach (Building_StorageCore core in gameComp.GetAllCores())
                {
                    if (core != null && core.Spawned && core.Powered && core.Map == map)
                    {
                        tradableCells.Add(core.Position);
                    }
                }

                __result = tradableCells;
            }
        }
    }
}

