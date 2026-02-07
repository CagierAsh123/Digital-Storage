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
    /// 交易行为兼容：交易完成后从虚拟存储扣除物品
    /// </summary>

    /// <summary>
    /// 来访商人交易：卖出虚拟存储物品时从核心扣除
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
                // 使用 DeductVirtualItems 按 ThingDef 扣除（处理有 stuff 的情况）
                int deducted = 0;
                if (tradeInfo.stuffDef != null)
                {
                    // 有 stuff 的物品，用精确匹配的 ExtractItem
                    Thing extracted = tradeInfo.sourceCore.ExtractItem(tradeInfo.def, countToGive, tradeInfo.stuffDef);
                    if (extracted != null)
                    {
                        deducted = extracted.stackCount;
                        extracted.Destroy(DestroyMode.Vanish);
                    }
                }
                else
                {
                    // 无 stuff 的物品，用 DeductVirtualItems
                    deducted = tradeInfo.sourceCore.DeductVirtualItems(tradeInfo.def, countToGive);
                }

                if (DigitalStorageSettings.enableDebugLog)
                {
                    Log.Message($"[DigitalStorage] Trade deducted (visitor): {tradeInfo.def.label} x{deducted}/{countToGive}");
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
    /// 
    /// 原版 LaunchThingsOfType 按 ThingDef+数量 从信标范围内收集物品并销毁。
    /// 
    /// 策略：完全接管此方法。
    /// 1. 先复刻原版逻辑，从地图信标范围内扣除物理物品
    /// 2. 如果物理物品不够，再从虚拟存储补扣剩余
    /// </summary>
    [HarmonyPatch(typeof(TradeUtility), "LaunchThingsOfType")]
    public static class Patch_TradeUtility_LaunchThingsOfType
    {
        public static bool Prefix(ThingDef resDef, int debt, Map map, TradeShip trader)
        {
            if (debt <= 0 || map == null || resDef == null)
            {
                return true;
            }

            // 检查是否有虚拟存储
            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null || gameComp.GetAllCores().Count == 0)
            {
                return true; // 没有核心，走原版逻辑
            }

            int remaining = debt;

            // ===== 第一步：从地图信标范围内扣除物理物品（复刻原版逻辑） =====
            List<Building_OrbitalTradeBeacon> beacons = Building_OrbitalTradeBeacon.AllPowered(map).ToList();
            
            // 收集信标范围内的所有匹配物品
            List<Thing> launchableThings = new List<Thing>();
            foreach (Building_OrbitalTradeBeacon beacon in beacons)
            {
                foreach (IntVec3 cell in beacon.TradeableCells)
                {
                    List<Thing> thingsAtCell = map.thingGrid.ThingsListAt(cell);
                    for (int i = 0; i < thingsAtCell.Count; i++)
                    {
                        Thing thing = thingsAtCell[i];
                        if (thing.def == resDef)
                        {
                            launchableThings.Add(thing);
                        }
                    }
                }
            }

            // 从物理物品中扣除
            for (int i = 0; i < launchableThings.Count && remaining > 0; i++)
            {
                Thing thing = launchableThings[i];
                if (thing == null || thing.Destroyed)
                {
                    continue;
                }

                // 跳过核心 SlotGroup 中的预留物品（这些由虚拟存储管理）
                bool isInCore = false;
                foreach (Building_StorageCore core in gameComp.GetAllCores())
                {
                    if (core != null && core.Spawned && core.Map == map)
                    {
                        SlotGroup sg = core.GetSlotGroup();
                        if (sg != null && sg.CellsList.Contains(thing.Position))
                        {
                            isInCore = true;
                            break;
                        }
                    }
                }
                if (isInCore)
                {
                    continue; // 预留物品不在这里扣，后面从虚拟存储统一扣
                }

                int toTake = System.Math.Min(thing.stackCount, remaining);
                if (toTake >= thing.stackCount)
                {
                    remaining -= thing.stackCount;
                    // 给商人（原版行为）
                    trader?.GiveSoldThingToTrader(thing, thing.stackCount, TradeSession.playerNegotiator);
                    // 如果 GiveSoldThingToTrader 没有销毁它，手动销毁
                    if (!thing.Destroyed)
                    {
                        thing.Destroy(DestroyMode.Vanish);
                    }
                }
                else
                {
                    remaining -= toTake;
                    Thing splitOff = thing.SplitOff(toTake);
                    trader?.GiveSoldThingToTrader(splitOff, splitOff.stackCount, TradeSession.playerNegotiator);
                    if (!splitOff.Destroyed)
                    {
                        splitOff.Destroy(DestroyMode.Vanish);
                    }
                }
            }

            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[DigitalStorage] LaunchThingsOfType: {resDef.label}, debt={debt}, after physical deduction remaining={remaining}");
            }

            // ===== 第二步：从虚拟存储补扣剩余 =====
            if (remaining > 0)
            {
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

                    int deducted = core.DeductVirtualItems(resDef, remaining);
                    remaining -= deducted;

                    if (DigitalStorageSettings.enableDebugLog && deducted > 0)
                    {
                        Log.Message($"[DigitalStorage] Orbital trade deducted from virtual: {resDef.label} x{deducted}");
                    }
                }
            }

            if (remaining > 0)
            {
                Log.Warning($"[DigitalStorage] LaunchThingsOfType: could not fully satisfy debt for {resDef.label}, shortfall={remaining}");
            }

            // 完全接管，不执行原版逻辑
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
