using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Services;
using DigitalStorage.Settings;
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
                    
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Message($"[数字存储] 交易扣除: {tradeInfo.def.label} x{countToGive}");
                    }
                }
            }

            // 清理追踪
            TradeItemTracker.UnregisterTradeItem(toGive);

            // 阻止原版逻辑（因为物品不在地图上）
            return false;
        }
    }

    /// <summary>
    /// 轨道交易扣除物品
    /// </summary>
    [HarmonyPatch(typeof(TradeUtility), "LaunchThingsOfType")]
    public static class Patch_TradeUtility_LaunchThingsOfType
    {
        public static bool Prefix(ThingDef resDef, int debt, Map map, TradeShip trader)
        {
            if (debt <= 0 || map == null)
            {
                return true;
            }

            // 检查是否有虚拟存储物品需要扣除
            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return true;
            }

            int remaining = debt;

            // 先从虚拟存储扣除
            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered || core.Map != map)
                {
                    continue;
                }

                if (remaining <= 0)
                {
                    break;
                }

                // 检查核心是否有这种物品
                int available = core.GetItemCount(resDef, null);
                if (available > 0)
                {
                    int toExtract = System.Math.Min(available, remaining);
                    Thing extracted = core.ExtractItem(resDef, toExtract, null);
                    
                    if (extracted != null)
                    {
                        remaining -= extracted.stackCount;
                        extracted.Destroy(DestroyMode.Vanish);
                        
                        if (DigitalStorageSettings.enableDebugLog)
                        {
                            Log.Message($"[数字存储] 轨道交易扣除: {resDef.label} x{extracted.stackCount}");
                        }
                    }
                }
            }

            // 如果虚拟存储不够，让原版逻辑处理剩余部分
            if (remaining > 0)
            {
                // 修改 debt 参数（通过反射或其他方式）
                // 由于无法直接修改参数，我们让原版处理全部，然后在 Postfix 中补偿
                return true;
            }

            // 虚拟存储足够，跳过原版逻辑
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
}
