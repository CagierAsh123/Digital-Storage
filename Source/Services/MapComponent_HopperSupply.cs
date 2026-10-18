using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Services
{
    /// <summary>
    /// <b>料斗（<c>Hopper</c>）的隔空投料。</b>
    ///
    /// <para><b>断在哪</b>：给料斗投料的是 <c>WorkGiver_CookFillHopper.HopperFillFoodJob</c>，
    /// 它的候选来源是 <c>pawn.Map.listerThings.ThingsOfDef/ThingsInGroup</c> —— 纯地图扫描。
    /// 本 mod 的内容物是容器里未 Spawned 的真实 Thing（硬约束：内容物绝不进 <c>listerThings</c>）
    /// ⇒ 核心里的生食对营养膏机完全不存在；丢在地上的生食又会被自动收纳吸回核心。
    /// 结果就是营养膏机（含 1.6 的其它吃料斗的机器）永远没有原料。</para>
    ///
    /// <para><b>为什么用 MapComponent 而不是打 Tick 补丁</b>：料斗的 Def 是
    /// <c>&lt;tickerType&gt;Never&lt;/tickerType&gt;</c>（<c>Buildings_Production.xml:1850</c>），
    /// 它的类 <c>Building_Storage</c> 自己也没有 <c>Tick</c> —— 根本没有可挂的每帧入口。
    /// <c>Map</c> 会自动实例化所有非抽象 <c>MapComponent</c> 子类（<c>Map.cs:713</c>），
    /// <c>MapComponentTick</c> 每 tick 都被 <c>MapComponentUtility</c> 调（并自带 try/catch），
    /// 于是这里成了最省事的"给不 tick 的建筑补一个节拍"的地方。</para>
    ///
    /// <para><b>闸门全部抄原版</b>（<c>WorkGiver_CookFillHopper</c>）：</para>
    /// <list type="bullet">
    /// <item>只看**格子上**已有的是不是原料（<c>Building_NutrientPasteDispenser.IsAcceptableFeedstock</c>），
    /// 那一堆超过 35% 堆叠就不再补（原版同一道闸，免得把料斗一直塞爆）；</item>
    /// <item>格子上已有原料时**只补同一个 def**（原版就是拿 <c>firstItem.def</c> 去 <c>ThingsOfDef</c> 找的）；</item>
    /// <item>候选必须是 <c>IsNutritionGivingIngestible</c> 且 preferability 为 <c>RawBad/RawTasty</c>（生食），
    /// 且料斗自己的过滤器 <c>AllowedToAccept</c> 认它；</item>
    /// <item>落地用 <c>ThingPlaceMode.Direct</c> —— 原版搬运工把料放上格子走的就是这一条
    /// （<c>Toils_Haul.PlaceHauledThingInCell:347</c> 的 <c>TryDropCarriedThing(…, Direct, …)</c>），
    /// 放不下就由 <see cref="CoreSupply.TakeAndPlace"/> 原样还回核心。</item>
    /// </list>
    ///
    /// <para><b>唯一没抄的一道闸：优先级</b>。原版会跳过"当前储存优先级 ≥ 料斗优先级"的料
    /// （<c>StoreUtility.CurrentStoragePriorityOf(thing) &gt;= 料斗优先级</c>），本补丁**故意不抄** ——
    /// 那道闸管的是"别让 pawn 从更好的**格子储存**里把货搬走"，而核心是虚拟整仓：
    /// 玩家把核心设成「重要」（很常见）时，抄了它就会把料斗彻底饿死，与「任何位置隔空取放、不加限制」
    /// 的既定方向冲突。</para>
    /// </summary>
    public class MapComponent_HopperSupply : MapComponent
    {
        /// <summary>原版 <c>WorkGiver_CookFillHopper.JobOnThing</c> 里的 0.35f：格子上那堆超过它就不再补。</summary>
        private const float AlreadyFilledFraction = 0.35f;

        private static readonly List<Thing> tmpCandidates = new List<Thing>();

        public MapComponent_HopperSupply(Map map) : base(map)
        {
        }

        public override void MapComponentTick()
        {
            // 相位节流：每 15 tick 扫一次本图料斗（0.25 秒内到账，人眼无差别）。
            if ((Find.TickManager.TicksGame + map.uniqueID) % 15 != 0) return;

            List<Thing> hoppers;
            try
            {
                // 原版自己也是这么找料斗的（WorkGiver_CookFillHopper 用 ThingRequest.ForDef(Hopper)）。
                hoppers = map.listerThings.ThingsOfDef(ThingDefOf.Hopper);
            }
            catch (Exception)
            {
                return; // 列表被别的东西改坏了：这一轮算了，下一轮再来
            }
            if (hoppers == null || hoppers.Count == 0) return;

            // 候选表**一轮只汇总一次**：GatherContents 是 O(核心数 × 核心内容物数)，
            // 原先每台料斗都各扫一遍（20 台料斗 + 3000 堆 = 每轮 6 万次迭代）。
            // 快照里被前一台料斗取走的堆会失效（ParentHolder 变 null / Destroyed），TrySupply 逐件复查。
            tmpCandidates.Clear();
            try
            {
                CoreSupply.GatherContents(map, hoppers[0].Position, tmpCandidates);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 料斗候选汇总异常（本轮跳过）：" + e, 0x44534851);
                return;
            }
            if (tmpCandidates.Count == 0) return; // 没料可投，整轮省掉

            for (int i = 0; i < hoppers.Count; i++)
            {
                try
                {
                    TrySupply(hoppers[i], tmpCandidates);
                }
                catch (Exception e)
                {
                    Log.ErrorOnce("[DigitalStorage] 料斗投料异常（这一台跳过，其余继续）：" + e, 0x44534850);
                }
            }
        }

        private void TrySupply(Thing thing, List<Thing> candidates)
        {
            if (thing == null || thing.Destroyed || !thing.Spawned) return;

            Building_Storage hopper = thing as Building_Storage;
            if (hopper == null || hopper.def == null || hopper.def.building == null) return;
            if (!hopper.def.building.isHopper) return;

            SlotGroup slotGroup = hopper.GetSlotGroup();
            if (slotGroup == null || slotGroup.Settings == null) return;

            IntVec3 cell = hopper.Position;

            // ① 原版第一道闸：格子上已有的原料堆占了多少（原版取"最后一个命中的"）。
            Thing existing = null;
            List<Thing> cellThings = cell.GetThingList(map);
            for (int i = 0; i < cellThings.Count; i++)
            {
                Thing t = cellThings[i];
                if (t == null || t.def == null) continue;
                if (t.def.category != ThingCategory.Item) continue; // 料斗自己也在格子上，跳过建筑
                if (!Building_NutrientPasteDispenser.IsAcceptableFeedstock(t.def)) continue;
                existing = t;
            }

            int capacity;
            if (existing != null)
            {
                int stackLimit = Math.Max(1, existing.def.stackLimit);
                if ((float)existing.stackCount / stackLimit > AlreadyFilledFraction) return; // 原版同一道闸
                capacity = stackLimit - existing.stackCount;
            }
            else
            {
                capacity = 0; // 空格子：下面按挑中的 def 的 stackLimit 定（原版 fitInStoreCell 语义）
            }
            if (existing != null && capacity <= 0) return;

            ReservationManager resMgr = map.reservationManager;
            if (resMgr != null && resMgr.IsReservedByAnyoneOf(hopper, Faction.OfPlayer)) return; // 有搬运工在路上

            // ② 从核心挑一件（判据全是原版自己的）。候选表由调用方一轮汇总一次，这里只读。

            Thing pick = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                Thing t = candidates[i];
                if (t == null || t.Destroyed || t.def == null) continue;
                if (!(t.ParentHolder is Building_StorageCore)) continue; // 只搬核心里的
                if (t.IsForbidden(Faction.OfPlayer)) continue;
                if (resMgr != null && resMgr.IsReservedByAnyoneOf(t, Faction.OfPlayer)) continue;
                if (existing != null && t.def != existing.def) continue; // 格子上有料时只补同一个 def
                if (!t.def.IsNutritionGivingIngestible) continue;
                if (t.def.ingestible == null) continue;

                FoodPreferability preferability = t.def.ingestible.preferability;
                if (preferability != FoodPreferability.RawBad && preferability != FoodPreferability.RawTasty) continue;

                if (!Building_NutrientPasteDispenser.IsAcceptableFeedstock(t.def)) continue;
                if (!slotGroup.Settings.AllowedToAccept(t)) continue; // 料斗自己的过滤器

                pick = t;
                break;
            }
            if (pick == null) return;

            // ③ 数量：空格子给一整堆（= 原版 fitInStoreCell），有料就把那一堆补满。
            int count = existing != null
                ? Math.Min(pick.stackCount, capacity)
                : Math.Min(pick.stackCount, Math.Max(1, pick.def.stackLimit));
            if (count <= 0) return;

            // ④ 直塞到料斗那一格。放不进去 ⇒ TakeAndPlace 把货原样还回核心，一件不丢。
            CoreSupply.TakeAndPlace(pick, count,
                taken => GenPlace.TryPlaceThing(taken, cell, map, ThingPlaceMode.Direct));
        }
    }
}
