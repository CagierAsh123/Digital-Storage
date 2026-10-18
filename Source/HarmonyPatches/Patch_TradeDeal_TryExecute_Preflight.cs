using System;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// A3（审计 2026-10-17）：交易确认前，把**卖单**夹到当前真实库存。
    ///
    /// <para>原版卖出链路是 <c>Tradeable.ResolveTrade</c>（<c>Tradeable.cs:463-480</c>）→
    /// <c>TransferableUtility.TransferNoSplit(thingsColony, CountToTransfer)</c>
    /// （<c>TransferableUtility.cs:48-80</c>）：它逐件取 <c>min(剩余, thing.stackCount)</c>，
    /// 拿不到的货直接跳过，收尾只 <c>Log.Error("Can't transfer things because there is nothing left.")</c>
    /// —— 不抛、不回滚。而 <c>TradeDeal.TryExecute</c> 在逐条 <c>ResolveTrade</c> **之前**就按
    /// <c>CurTotalCurrencyCostForDestination</c> 把银钱计进交易额 ⇒ 开窗期间货消失
    /// （生鲜腐烂 / 核心断电、被拆 / 筛选、优先级变更）就会白拿钱。</para>
    ///
    /// <para>修法：<c>TryExecute</c> 之先（此时 <c>UpdateCurrencyCount</c> 还没跑）用公开 API
    /// <c>Tradeable.ForceToDestination</c> 把超卖的那几条夹到实际库存，并给玩家一条提示。
    /// 不拦交易、不改原版 IL、不动 out 参数 —— 夹完让原版照常算钱和执行。</para>
    /// </summary>
    [HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.TryExecute))]
    internal static class Patch_TradeDeal_TryExecute_Preflight
    {
        // 提示里最多列几条货，其余用省略号带过（避免一次卖 30 种货时消息刷屏）。
        private const int MaxListedDefs = 4;

        private static void Prefix(TradeDeal __instance)
        {
            if (__instance == null) return;
            // 赠礼模式不走付款，夹了反而会改玩家要送多少。
            if (TradeSession.giftMode) return;

            try
            {
                string notice = ClampOversold(__instance);
                if (notice == null) return;
                Messages.Message("DS_TradePreflight_Clamped".Translate(notice), MessageTypeDefOf.CautionInput, false);
            }
            catch (Exception e)
            {
                // 预检永远不许卡住交易：夹失败就放原版流程走（最坏是回到原版行为）。
                Log.ErrorOnce("[DigitalStorage] 交易结算前夹卖单失败（已放行原版流程）: " + e, 0x5D52A);
            }
        }

        /// <summary>把超卖的卖单夹到实际库存；返回给玩家看的清单，没有超卖返回 null。</summary>
        private static string ClampOversold(TradeDeal deal)
        {
            List<Tradeable> all = deal.AllTradeables;
            if (all == null) return null;

            StringBuilder sb = null;
            int shown = 0;
            int clamped = 0;
            for (int i = 0; i < all.Count; i++)
            {
                Tradeable t = all[i];
                // 银钱行由 CurrencyTradeable 自己维护，不在这里动。
                if (t == null || t.IsCurrency) continue;
                if (t.ActionToDo != TradeAction.PlayerSells) continue;

                int want = t.CountToTransferToDestination;
                if (want <= 0) continue;

                int have = CountColonyLeft(t);
                if (have >= want) continue;

                t.ForceToDestination(have);
                clamped++;
                if (shown >= MaxListedDefs) continue;

                ThingDef def = t.ThingDef;
                string label = def?.label ?? def?.defName ?? "?";
                if (sb == null) sb = new StringBuilder();
                else sb.Append(", ");
                sb.Append(label).Append(' ').Append(have).Append('/').Append(want);
                shown++;
            }

            if (clamped == 0) return null;
            if (sb == null) return "...";
            if (clamped > shown) sb.Append(", ...");
            return sb.ToString();
        }

        /// <summary>
        /// 这条卖单在殖民地手上还剩多少 —— 直接数原版 <c>ResolveTrade</c> 将要消费的那个列表
        /// （<c>Tradeable.thingsColony</c>，开窗时抓的引用），所以口径与原版逐一对应，不会误判。
        /// </summary>
        private static int CountColonyLeft(Tradeable t)
        {
            List<Thing> things = t.thingsColony;
            if (things == null) return 0;
            int sum = 0;
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (thing == null || thing.Destroyed) continue;
                sum += thing.stackCount;
            }
            return sum;
        }
    }
}
