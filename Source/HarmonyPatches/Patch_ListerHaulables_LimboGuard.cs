using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>别让"游离物品"混进搬运列表</b> —— 2026-10-22 报障的止血那一半。
    ///
    /// <para><b>报障链条</b>：小人搬东西进核心时日志先出现
    /// <c>Tried to add BlocksSandstone158578 to ThingOwner but this thing is already in another container.
    /// owner=DigitalStorage_StorageCore103138, current container owner=null.</c>
    /// 之后那块砖既不在图上（<c>Map == null</c>）也不属于任何容器，接着每个"结束当前任务"的小人
    /// 都刷一条 <c>NullReferenceException at Verse.GridsUtility.Fogged</c>。</para>
    ///
    /// <para><b>为什么"Map 为空"就一定会炸</b>：<c>GridsUtility.Fogged(Thing t)</c> 是
    /// <c>t.MapHeld.fogGrid.IsFogged(t.PositionHeld)</c>；<c>Thing.MapHeld</c> 沿
    /// <c>holdingOwner.Owner</c> 链找地图，链断了就是 null。
    /// 而 <c>Pawn_JobTracker.TryOpportunisticJob</c> 的顺手搬运扫描遍历
    /// <c>map.listerHaulables.ThingsPotentiallyNeedingHauling()</c>，
    /// 对每一件调 <c>HaulAIUtility.PawnCanAutomaticallyHaulFast_NewTemp</c>，第一行就是 <c>t.Fogged()</c>。
    /// 原版 <c>ListerHaulables.ShouldBeHaulable</c> <b>不看 <c>Map</c></b>，
    /// 所以只要这种物品进过一次列表，就会一直刷。</para>
    ///
    /// <para><b>这里只做一件最小的事</b>：<c>MapHeld == null</c> 的东西永远不该被判成
    /// "值得去搬" —— 它连一个能放下的地图都没有，原版拿它也没法干活，只会空引用。
    /// 判据取 <c>MapHeld</c> 而不是 <c>Spawned</c>：核心/背包/随身携带里的内容物是
    /// despawned 但 <c>MapHeld</c> 有值，它们**必须**照旧参与搬运判定（那是本 mod 的搬出路径）。</para>
    /// </summary>
    [HarmonyPatch(typeof(ListerHaulables), "ShouldBeHaulable")]
    internal static class Patch_ListerHaulables_ShouldBeHaulable_Limbo
    {
        private static bool Prefix(Thing t, ref bool __result)
        {
            if (t == null || t.Destroyed)
            {
                __result = false;
                return false;
            }
            if (t.MapHeld != null) return true;

            // 无主物品：不判"可搬"，否则它会长期占着 listerHaulables 并让原版扫描空引用
            __result = false;
            return false;
        }
    }

    /// <summary>
    /// <b>已有存档里的游离物品：救出来</b> —— 上面那道闸只防"新进"，老存档里已经躺进
    /// <c>listerHaulables</c> 的东西需要有人把它捞出来，否则红字会一直刷到玩家放弃这个档。
    ///
    /// <para>每 256 tick 随 <c>ListerHaulables.ListerHaulablesTick</c> 收一次
    /// （按地图 <c>uniqueID</c> 错峰，避免多图同 tick 全扫）：
    /// 遍历列表里 <c>MapHeld == null && !Destroyed</c> 的物品 ⇒
    /// <b>先塞进最近的核心</b>（这正是玩家原本在做的操作）⇒ 塞不进就落地到核心旁 ⇒
    /// 都不行就从列表里摘掉（它已经搬不了了，留着只会刷屏），并留一条日志。</para>
    ///
    /// <para>救的对象是**真实物品**：<c>Verse.GenLeaving</c> 在"落地失败"时会
    /// <c>break</c> 并让剩下的东西留在一个 <c>new ThingOwner&lt;Thing&gt;()</c>（<c>Owner == null</c>）里，
    /// 那就是报障日志里 <c>current container owner=null</c> 的来源。这类物品本身没有任何列表引用它，
    /// 只有"混进了搬运列表"这一种可被抓到的状态 —— 抓到了就顺手救回。</para>
    /// </summary>
    [HarmonyPatch(typeof(ListerHaulables), "ListerHaulablesTick")]
    internal static class Patch_ListerHaulables_LimboRescue
    {
        private const int SweepInterval = 256;

        /// <summary><c>ListerHaulables.map</c> 是 private readonly，取一次引用委托，热路径无反射开销。</summary>
        private static readonly AccessTools.FieldRef<ListerHaulables, Map> MapOf =
            AccessTools.FieldRefAccess<ListerHaulables, Map>("map");

        private static readonly List<Thing> scratch = new List<Thing>();

        private static void Postfix(ListerHaulables __instance)
        {
            Map map = MapOf(__instance);
            if (map == null) return;
            if ((Find.TickManager.TicksGame + map.uniqueID) % SweepInterval != 0) return;

            ICollection<Thing> haulables = __instance.ThingsPotentiallyNeedingHauling();
            if (haulables == null || haulables.Count == 0) return;

            scratch.Clear();
            foreach (Thing t in haulables)
            {
                if (t == null) continue;
                if (t.Destroyed || t.MapHeld == null) scratch.Add(t);
            }
            if (scratch.Count == 0) return;

            int saved = 0;
            int dropped = 0;
            for (int i = 0; i < scratch.Count; i++)
            {
                Thing t = scratch[i];
                if (t == null) continue;

                if (!t.Destroyed)
                {
                    try
                    {
                        if (TrySalvage(map, t)) saved++;
                    }
                    catch (Exception e)
                    {
                        Log.ErrorOnce("[DigitalStorage] 救游离物品时出错：" + e, 0x4C494D42);
                    }
                }

                // 无论救没救回来，它都不该继续待在搬运列表里：
                // 救回来的已经落到地上了（下一轮 Check 会重新判定），没救回来的搬不了。
                __instance.Notify_DeSpawned(t);
                dropped++;
            }

            if (saved > 0 || dropped > 0)
            {
                Log.Warning("[DigitalStorage] 搬运列表里发现游离物品（不属于任何容器、没有地图，会让小人搬运时刷空引用）："
                            + "救回 " + saved + " 件、摘掉 " + dropped + " 件。地图=" + map);
            }
            scratch.Clear();
        }

        /// <summary>
        /// 给游离物品找去处：先当未落地物品塞进最近的核心，再落到地面。
        ///
        /// <para>注意先 <c>holder.Remove(t)</c> 清指针：它可能还挂在一个**无主容器**上，
        /// 不清掉的话任何 <c>TryAdd</c> 都只会打一条 "already in another container" 并拒绝。</para>
        /// </summary>
        private static bool TrySalvage(Map map, Thing t)
        {
            if (t == null || t.Destroyed) return false;

            ThingOwner holder = t.holdingOwner;
            if (holder != null) holder.Remove(t);
            if (t.Spawned) return true;   // 其实在地图上（MapHeld 为 null 不可能，防御一下）

            Building_StorageCore core = DigitalDropRedirect.NearestUsableCore(map, map.Center);
            if (core != null && core.TryStoreIgnoringFilter(t, t.stackCount) == t.stackCount) return true;

            IntVec3 cell = (core != null) ? core.PositionHeld : map.Center;
            if (cell.IsValid && GenPlace.TryPlaceThing(t, cell, map, ThingPlaceMode.Near, out Thing _)) return true;

            return false;
        }
    }
}
