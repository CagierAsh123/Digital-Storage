using DigitalStorage.AI;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 制作代理取活时，放宽 <c>WorkGiver_DoBill.ThingIsUsableBillGiver</c> 的台子白名单。
    ///
    /// <para><b>原版逻辑</b>（<c>RimWorld\WorkGiver_DoBill.cs:361-405</c>）：
    /// <code>
    /// if (def.fixedBillGiverDefs != null &amp;&amp; def.fixedBillGiverDefs.Contains(thing.def)) return true;
    /// // 之后只剩 Pawn / Corpse 的几种特例（billGiversAllHumanlikes 等）
    /// return false;
    /// </code>
    /// <c>workGivers.xml</c> 里 19 个 DoBill giver 有 16 个带精确 defName 列表
    /// （<c>DoBillsCook</c> = ElectricStove/FueledStove，<c>DoBillsStonecut</c> = TableStonecutter …
    /// 见 <c>Data\*\Defs\WorkGiverDefs\WorkGivers.xml</c>）⇒ **任何 mod 工作台都不在这张名单里**，
    /// 代理调 <c>JobOnThing</c> 一律 null，表现为"菜单能选、就是不干活"。</para>
    ///
    /// <para><b>这里只放宽 <c>thing is Building</c> 且作用域已打开的情况</b>
    /// （<see cref="BenchWhitelistScope.Relaxed"/>，见那边的长注释），Pawn / Corpse 一律交回原版。
    /// 放宽后仍由原版继续判：<c>bill.recipe.requiredGiverWorkType</c> 不匹配的 giver 在
    /// <c>StartOrResumeBillJob</c> 里照样拿不到 job（这是原版给"配方指定工作类型"留的门，
    /// 不能替它放宽）。</para>
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_DoBill), nameof(WorkGiver_DoBill.ThingIsUsableBillGiver))]
    internal static class Patch_BenchWhitelist_Relax
    {
        private static bool Prefix(Thing thing, ref bool __result)
        {
            if (!BenchWhitelistScope.Relaxed) return true;
            if (!(thing is Building)) return true;

            // Building 走完白名单之后原版只会再判 Pawn/Corpse 特例（都不是 Building）⇒ 直接给 true。
            __result = true;
            return false;
        }
    }
}
