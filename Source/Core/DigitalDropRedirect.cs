using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using Verse;

namespace DigitalStorage.Core
{
    /// <summary>
    /// <b>代理干活时的"掉落直塞数字存储"作用域</b> —— 治卡顿的第二半（省成本，不是减产量）。
    ///
    /// <para><b>省的是什么（2026 更正）</b>：落地要走
    /// <c>TryPlaceDirect → SpawnSetup</c>：注册进 <c>thingGrid</c> / <c>listerThings</c> /
    /// region 列表，并让地图网格变脏（后面还要重建）。直塞进容器把这一整套跳过
    /// —— 容器内容不进 <c>listerThings</c> 是本 mod 的铁律，所以注册与 region 变脏都免了。</para>
    ///
    /// <para>⚠️ <b>别再说"省掉搜附近空位"</b>：<c>ThingPlaceMode.Near</c> 确实会
    /// <c>TryFindPlaceSpotNear</c> 搜空位，但挖空的格子本身就是空的、中心格基本立刻命中，
    /// 所以那一步不贵。省的是**注册**。<b>而且"落地注册 vs 销毁/地形变更"各占多少，尚无实测</b>
    /// —— 靠 <c>Drops</c> / <c>DropsDirect</c> 两个探针回答。</para>
    ///
    /// <para><b>对什么有用</b>：核心过滤器收的东西（矿脉产物、收获作物、拆除返还材料）✔；
    /// <b>石块不行</b> —— <c>Building_StorageCore.Accepts</c> 只看过滤器 + 容量，
    /// 而过滤器一般不含石块 ⇒ 直塞失败、退回原版落地。</para>
    ///
    /// <para><b>怎么接</b>：<c>CompDigitalWorker.CompTick</c> 在用假工人干活前
    /// <see cref="Begin"/>（选好本次 tick 的收件核心），干完 <see cref="End"/>；
    /// 期间任何 <c>GenPlace.TryPlaceThing</c> 由补丁改成直塞。</para>
    ///
    /// <para><b>失败必须放回地面</b>：核心不收（过滤器不认这种东西）、收不下、或图上没有可用核心
    /// ⇒ 一律返回 false 让原版照常落地。**绝不吞东西**。</para>
    /// </summary>
    internal static class DigitalDropRedirect
    {
        private static Building_StorageCore core;

        /// <summary>本次 tick 的收件核心；null = 不接管（零开销路径）。</summary>
        public static Building_StorageCore Core
        {
            get { return core; }
        }

        public static bool Active
        {
            get { return core != null; }
        }

        public static void Begin(Map map, IntVec3 near)
        {
            core = FindNearestUsableCore(map, near);
        }

        public static void End()
        {
            core = null;
        }

        /// <summary>
        /// 离 <paramref name="near"/> 最近的**可用核心**（没有则 null）。
        ///
        /// <para>给"制作代理的产物直塞"用：那边没有"整个 tick 一个收件人"的作用域语义，
        /// 是逐件产物现挑核心，所以需要一个能单独调用的入口。</para>
        /// </summary>
        public static Building_StorageCore NearestUsableCore(Map map, IntVec3 near)
        {
            return FindNearestUsableCore(map, near);
        }

        private static Building_StorageCore FindNearestUsableCore(Map map, IntVec3 near)
        {
            if (map == null) return null;
            List<Building_StorageCore> cores = CoreFinder.AllUsableCores(map);
            if (cores == null || cores.Count == 0) return null;

            Building_StorageCore best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < cores.Count; i++)
            {
                Building_StorageCore c = cores[i];
                if (c == null || !CoreFinder.IsUsable(c)) continue;
                float d = (c.Position - near).LengthHorizontalSquared;
                if (d < bestDist)
                {
                    bestDist = d;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>
        /// 把**尚未 spawn** 的物品直接放进核心容器。
        ///
        /// <para>与 <c>CompAutoIngest.TryIngest</c> 的差别只有一处、但很关键：**不做 <c>DeSpawn()</c>**。
        /// 那边处理的是"地上的物品"（必须先 Despawn），而这里处理的是
        /// <c>ThingMaker.MakeThing</c> 刚造出来、还没落地的物品 ——
        /// <c>Thing.DeSpawn()</c> 对未 spawn 的东西会直接 <c>Log.Error("... which is not spawned.")</c> 并返回。</para>
        ///
        /// <para><c>ThingOwner.TryAdd</c> 对未 spawn 物品是安全的（它只查 null / 类型 / 重复 /
        /// <c>holdingOwner</c> / 容量与过滤器，不要求 <c>Spawned</c>）。</para>
        /// </summary>
        public static bool TryIngestUnspawned(Building_StorageCore target, Thing t)
        {
            if (target == null || t == null || t.Destroyed) return false;
            if (target.Map == null || t.Spawned || t.holdingOwner != null) return false;
            if (!target.Accepts(t)) return false;
            int count = t.stackCount;
            if (target.GetDirectlyHeldThings().GetCountCanAccept(t) < count) return false;
            return target.TryStore(t, count) == count;
        }
    }
}
