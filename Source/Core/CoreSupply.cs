using System;
using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using Verse;

namespace DigitalStorage.Core
{
    /// <summary>
    /// 「从数字存储核心往外取料」的共用原语（隔空投料）。
    ///
    /// <para><b>为什么需要它</b>：原版有一类建筑既不走 Bill，也不实现 <c>IHaulSource</c> 取料，
    /// 而是各自写一条 <c>GenClosest</c> 找**本图已 Spawned 的散落物**的 workgiver
    /// （亚核心扫描仪 / 生育舱 / 料斗）。那些 workgiver 看不见容器里的东西
    /// （内容物未 Spawned，按硬约束不进 <c>listerThings</c>），而玩家刚丢在地上的料又会被
    /// 自动收纳在 15 tick 内吸回核心 ⇒ 它们永远拿不到料。修法统一是「容器 → 目的地」直塞。</para>
    ///
    /// <para>本类只提供两件事，**不发明任何挑选规则**：</para>
    /// <list type="number">
    /// <item><see cref="GatherContents"/>：本图可用核心的**直接**内容物，就近核心优先；</item>
    /// <item><see cref="TakeAndPlace"/>：从原容器里取 N 个（整堆就摘、部分就 <c>SplitOff</c>）交给调用方放置，
    /// 放不进去就原样还回去 —— <b>一件不丢</b>。</item>
    /// </list>
    ///
    /// <para>「收不收」由各建筑自己的原版闸门回答（调用方在挑料时问），本类一律不插嘴。</para>
    /// </summary>
    internal static class CoreSupply
    {
        /// <summary>
        /// 本图可用核心（已通电），按离 <paramref name="near"/> 的距离升序。
        /// 排序只为**确定性**（复现时能说清"从哪个核心取的"），不是选择规则。
        /// </summary>
        public static List<Building_StorageCore> CoresNearestFirst(Map map, IntVec3 near)
        {
            List<Building_StorageCore> cores = CoreFinder.AllUsableCores(map);
            if (cores.Count > 1)
            {
                cores.Sort((a, b) => (a.Position - near).LengthHorizontalSquared
                    .CompareTo((b.Position - near).LengthHorizontalSquared));
            }
            return cores;
        }

        /// <summary>
        /// 把本图可用核心的**直接**内容物追加进 <paramref name="outThings"/>（就近核心优先）。
        ///
        /// <para>只收直接内容物、不递归：递归出来的东西（尸体里的 pawn、压缩建筑里的建筑）
        /// <c>ParentHolder</c> 不是核心，调用方那句 <c>t.ParentHolder is Building_StorageCore</c> 会漏掉它们
        /// —— 与跨图取料同一个口径（见 <c>RemoteIngredientProvider.Append</c>）。</para>
        /// </summary>
        public static void GatherContents(Map map, IntVec3 near, List<Thing> outThings)
        {
            if (map == null || outThings == null) return;

            List<Building_StorageCore> cores = CoresNearestFirst(map, near);
            for (int c = 0; c < cores.Count; c++)
            {
                Building_StorageCore core = cores[c];
                if (core == null || !core.IsUsableNow) continue;

                ThingOwner held = core.GetDirectlyHeldThings();
                if (held == null) continue;

                for (int i = 0; i < held.Count; i++)
                {
                    Thing t = held[i];
                    if (t == null || t.Destroyed) continue;
                    outThings.Add(t);
                }
            }
        }

        /// <summary>
        /// 把 <paramref name="t"/> 从它住的容器里取出 <paramref name="count"/> 个交给 <paramref name="place"/>。
        /// <paramref name="place"/> 返回 false ⇒ **原样还回原容器**；连容器都回不去 ⇒ 落到它原来的位置落地。
        /// 返回是否成功放进了目的地。
        ///
        /// <para>取出走「整堆 <c>Remove</c> / 部分 <c>SplitOff</c>」两分支：<c>SplitOff</c> 不触碰 owner，
        /// 原堆留在容器里且数量已减（与 <c>HaulSourceContents.ExtractTo</c> 同款）。</para>
        ///
        /// <para>⚠️ 取地图/坐标必须在摘出**之前**：摘出后 <c>ParentHolder</c> 就没了，
        /// <c>MapHeld</c>/<c>PositionHeld</c> 会变 null，兜底落地就没了落点。</para>
        /// </summary>
        public static bool TakeAndPlace(Thing t, int count, Func<Thing, bool> place)
        {
            if (t == null || t.Destroyed || place == null) return false;

            IThingHolder holder = t.ParentHolder as IThingHolder;
            if (holder == null) return false;
            if (holder is Building_StorageCore core && !core.HaulSourceEnabled) return false;

            ThingOwner owner = holder.GetDirectlyHeldThings();
            if (owner == null || !owner.Contains(t)) return false;

            int take = Math.Min(count, t.stackCount);
            if (take <= 0) return false;

            Map map = t.MapHeld;
            IntVec3 pos = t.PositionHeld;

            Thing taken;
            if (take >= t.stackCount)
            {
                owner.Remove(t);
                taken = t;
            }
            else
            {
                taken = t.SplitOff(take);
                if (taken == null) return false; // 拆不动（不可堆叠？）⇒ 原堆一动没动
            }

            if (place(taken)) return true;

            // 放不进去：还回原容器。SplitOff 出来的那件还没 Spawn，TryAdd 是安全的。
            if (!owner.TryAdd(taken, true) && map != null && pos.IsValid)
                GenPlace.TryPlaceThing(taken, pos, map, ThingPlaceMode.Near);
            return false;
        }
    }
}
