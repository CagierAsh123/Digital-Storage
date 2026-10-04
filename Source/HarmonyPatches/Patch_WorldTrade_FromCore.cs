using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>据点交易 / 远行队↔远行队交易 也能卖核心里的库存。</b>
    ///
    /// <para><b>交易窗口"玩家能卖什么"只有一个来源</b>：<c>TradeDeal.AddAllTradeables:46</c>
    /// 遍历 <c>ITrader.ColonyThingsWillingToBuy(playerNegotiator)</c>。四个实现里，两个在本图、
    /// 两个在世界地图：</para>
    ///
    /// <code>
    /// Pawn_TraderTracker.ColonyThingsWillingToBuy      → map.listerThings.AllThings + AllColonistBuildingsOfType&lt;IHaulSource&gt;()   ← 本图核心【本来就认】
    /// TradeShip.ColonyThingsWillingToBuy               → TradeUtility.AllLaunchableThingsForTrade(map)                          ← 有 Patch_AllLaunchableThingsForTrade
    /// Settlement_TraderTracker.ColonyThingsWillingToBuy → CaravanInventoryUtility.AllInventoryItems(caravan)                     ← ✗ 只认pawn口袋
    /// Caravan_TraderTracker.ColonyThingsWillingToBuy    → 同上                                                                    ← ✗ 只认pawn口袋
    /// </code>
    ///
    /// <para><c>CaravanInventoryUtility.AllInventoryItems</c>（<c>CaravanInventoryUtility.cs:18</c>）
    /// 是**逐 pawn 遍历 <c>pawn.inventory.innerContainer</c>** —— 只有"装在殖民者口袋里"的东西。
    /// 世界地图上根本没有"当前地图"，核心内容物一件都不在，所以据点交易窗口玩家侧看不到它们。</para>
    ///
    /// <para><b>剩下的闸门一个都不用管</b>：<c>TradeDeal.AddAllTradeables:52</c> 那行
    /// <c>if (!playerNegotiator.IsWorldPawn() &amp;&amp; !InSellablePosition(...))</c> 对世界地图交易
    /// **整条跳过**（谈判者就是 world pawn）；真正要放松的那道（<c>InSellablePosition</c> 的未 Spawn
    /// 类型白名单）已由 <c>Patch_TradeDeal_InSellablePosition</c> 负责，本图交易受益。</para>
    ///
    /// <para><b>成交转移天然安全</b>（4.0 是真实 Thing，所以不需要 3.0 那套造物+回滚）：
    /// <c>Tradeable.ResolveTrade:463</c> → <c>TransferableUtility.TransferNoSplit</c> →
    /// <c>ITrader.GiveSoldThingToTrader</c> → <c>toGive.SplitOff(count)</c>
    /// （<c>Thing.cs:1604</c>：整堆时 <c>DeSpawnOrDeselect()</c> + <c>holdingOwner?.Remove(this)</c>；
    /// <c>DeSpawnOrDeselect</c> 对未 Spawn 的东西有 <c>if (Spawned)</c> 守卫，
    /// 部分出售时 <c>SplitOff</c> 只会把原堆数量减掉）→ 进对方库存。核心里的余堆保持完整。</para>
    ///
    /// <para><b>作用域：所有地图上"可用状态"的本 mod 核心</b>（<see cref="HaulSourceContents.CoreSources"/>：
    /// 已生成 / 未毁 / 已通电 / 出库开关开着）—— 与跨图取料（<c>RemoteCoreSources</c>）用的是同一份判据。
    /// 世界地图上交易没有"本图"可言，所以这里是**全部地图**而不是"本图优先"；这也让"在据点把远处基地的货卖掉"
    /// 成为可能，与本 mod 隔空取放的既定方向一致。</para>
    ///
    /// <para><b>⚠️ 硬约束：绝不能让调用方的惰性序列塌掉。</b><c>TradeDeal.Reset()</c> 一旦在枚举途中拿到异常，
    /// 整张交易列表就是空的（表现"连白银都不显示"，与 mod 看起来毫无关系）。C# 不允许在 <c>try/catch</c> 里
    /// <c>yield return</c>，所以追加物必须**先在 try/catch 里物化成 List，再统一 yield** —— 最坏结果只是
    /// "少显示我们那部分"。同一条纪律见 <c>Patch_OrbitalTradeBeacon</c>。</para>
    /// </summary>
    [HarmonyPatch(typeof(Settlement_TraderTracker), nameof(Settlement_TraderTracker.ColonyThingsWillingToBuy))]
    internal static class Patch_SettlementTrade_FromCore
    {
        private static IEnumerable<Thing> Postfix(IEnumerable<Thing> __result)
        {
            return WorldTradeFromCore.Append(__result);
        }
    }

    /// <summary>远行队 ↔ 远行队交易（世界地图上两支队相遇）。修法与据点交易逐字相同。</summary>
    [HarmonyPatch(typeof(Caravan_TraderTracker), nameof(Caravan_TraderTracker.ColonyThingsWillingToBuy))]
    internal static class Patch_CaravanTrade_FromCore
    {
        private static IEnumerable<Thing> Postfix(IEnumerable<Thing> __result)
        {
            return WorldTradeFromCore.Append(__result);
        }
    }

    /// <summary>两条世界地图交易链共用的追加逻辑。</summary>
    internal static class WorldTradeFromCore
    {
        /// <summary>
        /// 原版"玩家可卖物品"序列 + 核心库存。
        ///
        /// <para>本方法是**迭代器**：先原样转发原版结果（不包 try/catch —— 原版自己的异常应当照原样暴露），
        /// 再把独立物化好的追加部分 yield 出去。</para>
        /// </summary>
        internal static IEnumerable<Thing> Append(IEnumerable<Thing> original)
        {
            // 原版容器与核心内容物可能指向同一件东西, 统一按身份去重
            var yielded = new HashSet<Thing>(ThingIdentityComparer.Instance);

            if (original != null)
            {
                foreach (Thing t in original)
                {
                    if (yielded.Add(t)) yield return t;
                }
            }

            List<Thing> additions = Collect();
            if (additions == null) yield break;

            for (int i = 0; i < additions.Count; i++)
            {
                Thing thing = additions[i];
                if (yielded.Add(thing)) yield return thing;
            }
        }

        /// <summary>
        /// 把**所有地图上可用核心**的直接内容物收成一个列表。任何异常都在这里被吞掉并降级为 null
        /// （调用方照旧显示原版内容），绝不把异常漏进原版的惰性序列。
        /// </summary>
        private static List<Thing> Collect()
        {
            try
            {
                List<Thing> result = new List<Thing>();

                List<Map> maps = Find.Maps;
                if (maps == null) return result;

                ITrader trader = TradeSession.trader;

                for (int i = 0; i < maps.Count; i++)
                {
                    // 与轨道交易共用同一套准入规则(可用/取出开关/可卖), 避免两处漂移
                    result.AddRange(DigitalStorageTradeUtility.CoreContents(maps[i], trader));
                }

                return result;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 追加核心库存到世界地图交易列表时抛异常"
                    + "（已降级为只显示原版内容，交易列表不会因此变空）: " + e, 0x5D52B);
                return null;
            }
        }
    }
}
