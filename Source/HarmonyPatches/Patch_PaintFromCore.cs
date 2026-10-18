using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>粉饰建筑 / 粉饰地面（1.6 的涂装）</b>把核心里的染料算在内。
    ///
    /// <para><b>断点在哪</b>：两个工作类型的染料搜索都不走 <c>lookInHaulSources</c>，而是直接
    /// <c>listerThings.ThingsOfDef(ThingDefOf.Dye)</c>：
    /// <c>RimWorld\PaintUtility.cs:12</c>（<c>FindNearbyDyes</c>，供 <c>JobOnThing/JobOnCell</c> 用）
    /// 与 <c>RimWorld\WorkGiver_PaintBuilding.cs:133</c> / <c>WorkGiver_PaintFloor.cs:127</c>
    /// （<c>ShouldPaintThing/ShouldPaintCell</c> 的 <c>checkDye</c> 分支，失败原因写的是
    /// <c>"NoIngredient"</c>）。染料全进核心 ⇒ 列表里没有可画的活。</para>
    ///
    /// <para><b>为什么要两道</b>：<c>HasJobOnThing/HasJobOnCell</c> 先问判据（<c>checkDye:true</c> 才算有活），
    /// 再调 <c>JobOnThing/JobOnCell</c> 真要 job；只补后者则判据一直返回
    /// <c>false</c>，扫描永远走不到建 job 那一步；只补前者则 <c>JobOnThing</c> 里
    /// <c>!tmpDye.Any()</c> 直接 <c>return null</c>（<c>WorkGiver_PaintBuilding.cs:46-49</c>）。</para>
    ///
    /// <para><b>必须物化，不能只塞候选</b>：下游 <c>JobDriver_PaintBuilding.cs:83-84</c> 是
    /// <c>Toils_Goto.GotoThing(B, ClosestTouch)</c> + <c>Toils_Haul.StartCarryThing(B)</c>，
    /// 既没有 <c>canGotoSpawnedParent</c> 也不会去容器里拿 ⇒ 容器里的染料会让 job 首 tick 就
    /// <c>FailOnDespawnedNullOrForbidden</c>。所以这里按本 mod 既有铁律
    /// <c>HaulSourceContents.ExtractToFeet</c> 把染料取到脚下（<c>Source\Core\HaulSourceContents.cs:307</c>）。</para>
    ///
    /// <para><b>取多少</b>：原版一次 job 最多画 <c>num2 = Mathf.Min(10, 染料总数)</c> 个目标
    /// （<c>WorkGiver_PaintBuilding.cs:56</c>），所以取 <c>Min(10, 核心存量)</c>；上游补丁整堆外甩
    /// 引发过"甩到地上→被自动收纳吸回→反复中断"的死循环（见 <c>Docs\开发日志.md</c> 的 B1 记录），
    /// 这里刻意夹量。</para>
    ///
    /// <para><b>不动的地方</b>：地图上本来就有染料时原版逻辑完全接管（本图优先、不跨图搬）；
    /// 判据翻案只走"<c>checkDye:false</c> 重跑同一个方法"这一条路，其余闸门（可画性、指定、
    /// 拆除冲突、燃烧、预约）由原版自己再判一遍，不存在我们抄漏一条的可能。</para>
    /// </summary>
    internal static class PaintFromCore
    {
        /// <summary>原版单次粉饰 job 的目标上限（<c>WorkGiver_PaintBuilding.cs:56</c>）。</summary>
        private const int MaxDyePerTrip = 10;

        private static readonly MethodInfo ShouldPaintThingMi =
            AccessTools.Method(typeof(WorkGiver_PaintBuilding), "ShouldPaintThing");

        private static readonly MethodInfo ShouldPaintCellMi =
            AccessTools.Method(typeof(WorkGiver_PaintFloor), "ShouldPaintCell");

        // 同 tick 内 HasJobOnThing/HasJobOnCell 会为每个候选目标问一遍染料，
        // 而核心库存扫描是 O(核心数×内容物)，所以按 (地图, tick) 记忆一次。
        private static int memoTick = -1;
        private static Map memoMap;
        private static Thing memoDye;

        /// <summary>核心里还能用的染料（未销毁、未被禁用、没被禁止）。</summary>
        internal static Thing FindDyeInStorage(Pawn pawn)
        {
            Map map = pawn?.MapHeld;
            if (map == null) return null;

            int now = Find.TickManager.TicksGame;
            if (memoMap == map && memoTick == now)
            {
                if (memoDye != null && !memoDye.Destroyed && memoDye.stackCount > 0) return memoDye;
                memoDye = null;
            }
            else
            {
                memoMap = map;
                memoTick = now;
                memoDye = HaulSourceContents.FindBestIncludingRemote(
                    map, null, t => t.def == ThingDefOf.Dye && !t.IsForbidden(pawn));
            }
            return memoDye;
        }

        internal static bool CanPaintWithStoredDye(Pawn pawn)
        {
            return FindDyeInStorage(pawn) != null;
        }

        /// <summary>把核心里的染料取到 <paramref name="pawn"/> 脚下，返回落地的那件（失败 null）。</summary>
        internal static Thing MaterializeDye(Pawn pawn, int maxAmount)
        {
            Thing best = FindDyeInStorage(pawn);
            if (best == null) return null;

            int amount = Mathf.Min(Mathf.Max(1, maxAmount), best.stackCount);
            Thing placed = HaulSourceContents.ExtractToFeet(best, amount, pawn);
            if (placed == null) return null;

            memoDye = null;          // 核心已经少了一叠，缓存作废
            memoTick = -1;

            if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog && ConsumePatchUtil.ShouldLog("PaintFromCore"))
            {
                Log.Message("[DigitalStorage] 粉饰从核心取染料: " + placed.def.defName + " x" + placed.stackCount + " → " + pawn.LabelShort);
            }
            return placed;
        }

        /// <summary>
        /// 判据翻案：原版这次失败可能不是"缺染料"，而是别的闸门。用
        /// <paramref name="checkDye"/> = <c>false</c> 重跑原方法，只有它这次给出 true 才翻。
        /// </summary>
        internal static bool RerunWithoutDyeGate(MethodInfo mi, object giver, Pawn pawn, object target, bool forced)
        {
            if (mi == null || giver == null) return false;
            try
            {
                object r = mi.Invoke(giver, new[] { (object)pawn, target, forced, false });
                if (r is AcceptanceReport report) return report.Accepted;
            }
            catch (System.Exception e)
            {
                Log.WarningOnce("[DigitalStorage] 粉饰判据重跑失败: " + e.Message, 0x5A17C0 ^ pawn.thingIDNumber);
            }
            return false;
        }

        internal static MethodInfo BuildingGateMi => ShouldPaintThingMi;

        internal static MethodInfo FloorGateMi => ShouldPaintCellMi;

        /// <summary>单次粉饰的上限（供 <c>Patch_DyeFromCore</c> 等其它染料入口复用同一份常量语义）。</summary>
        internal static int PreferredDyePerTrip => MaxDyePerTrip;
    }

    /// <summary>① 取物列表：原版 <c>RimWorld\PaintUtility.cs:12</c> 只扫地图。</summary>
    [HarmonyPatch(typeof(PaintUtility), nameof(PaintUtility.FindNearbyDyes))]
    internal static class Patch_PaintUtility_FindNearbyDyes
    {
        private static void Postfix(Pawn pawn, ref List<Thing> __result)
        {
            // 地图上已经有染料 ⇒ 原版结果照用（本图优先）。
            if (__result != null && __result.Count > 0) return;
            if (pawn == null || !ConsumePatchUtil.PawnMayUseCore(pawn)) return;

            Thing dye = PaintFromCore.MaterializeDye(pawn, PaintFromCore.PreferredDyePerTrip);
            if (dye == null) return;

            // 取到脚下后它自然满足原版校验（未禁止、可及、可预约），直接当作唯一候选。
            __result = new List<Thing> { dye };
        }
    }

    /// <summary>② 判据：原版 <c>WorkGiver_PaintBuilding.cs:133</c> 的 <c>checkDye</c> 分支。</summary>
    [HarmonyPatch(typeof(WorkGiver_PaintBuilding), "ShouldPaintThing")]
    internal static class Patch_WorkGiver_PaintBuilding_ShouldPaintThing
    {
        private static void Postfix(WorkGiver_PaintBuilding __instance, Pawn pawn, Thing t, bool forced, bool checkDye, ref AcceptanceReport __result)
        {
            if (__result) return;
            if (!checkDye) return;                       // 调用方没问染料，不是这一条拒的
            if (pawn == null || !ConsumePatchUtil.PawnMayUseCore(pawn)) return;
            if (!PaintFromCore.CanPaintWithStoredDye(pawn)) return;
            if (!PaintFromCore.RerunWithoutDyeGate(PaintFromCore.BuildingGateMi, __instance, pawn, t, forced)) return;

            __result = AcceptanceReport.WasAccepted;
        }
    }

    /// <summary>③ 判据（地面）：原版 <c>WorkGiver_PaintFloor.cs:127</c> 的 <c>checkDye</c> 分支。</summary>
    [HarmonyPatch(typeof(WorkGiver_PaintFloor), "ShouldPaintCell")]
    internal static class Patch_WorkGiver_PaintFloor_ShouldPaintCell
    {
        private static void Postfix(WorkGiver_PaintFloor __instance, Pawn pawn, IntVec3 c, bool forced, bool checkDye, ref AcceptanceReport __result)
        {
            if (__result) return;
            if (!checkDye) return;
            if (pawn == null || !ConsumePatchUtil.PawnMayUseCore(pawn)) return;
            if (!PaintFromCore.CanPaintWithStoredDye(pawn)) return;
            if (!PaintFromCore.RerunWithoutDyeGate(PaintFromCore.FloorGateMi, __instance, pawn, c, forced)) return;

            __result = AcceptanceReport.WasAccepted;
        }
    }
}
