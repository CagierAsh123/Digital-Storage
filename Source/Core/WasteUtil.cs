using RimWorld;
using Verse;

namespace DigitalStorage.Core
{
    /// <summary>
    /// 「有害垃圾」判定：有毒垃圾 / 废料包这一族（<c>ThingDefOf.Wastepack</c> 及其 mod 变体）。
    ///
    /// <para><b>为什么需要单列一类</b>（社区反馈 2026-10-18 第 1 条）：这类物品原版的清理链是
    /// <c>WorkGiver_EmptyWasteContainer</c> → <c>JobDriver_EmptyThingContainer</c>，而那条 driver
    /// <b>只支持格子目的地</b>（收尾是 <c>CarryHauledThingToCell</c> + <c>PlaceHauledThingInCell</c>）。
    /// 只要"最优目的地"是容器（例如高优先级的存储核心），原版就会把
    /// <c>foundCell = IntVec3.Invalid</c> 塞进 <c>job.targetC</c>，而
    /// <c>JobDriver_EmptyThingContainer.TryMakePreToilReservations</c> 会预订这个非法格子并失败
    /// ⇒ 作业永远建不起来、也没有任何报错（"小人就是不来清理"）。</para>
    ///
    /// <para>所以核心的**默认筛选**要把这一族关掉（关掉 = 它不再是"最优目的地"，原版回到格子腿，
    /// 玩家建一个垃圾储存区就能正常清理）。玩家仍可在筛选窗口里手动勾回来 —— 勾回来就意味着
    /// 承担"原版清空作业会被容器目的地搞坏"的后果（<c>Patch_EmptyWasteContainerFallback</c> 会兜一层）。</para>
    /// </summary>
    public static class WasteUtil
    {
        /// <summary>
        /// 是否属于"溶解/污染"族物品。用组件而不是 defName 判定，能覆盖 mod 的废料变体：
        /// 原版 <c>Wastepack</c> 带 <c>CompProperties_Dissolution</c> +
        /// <c>CompProperties_DissolutionEffectPollution</c>（见
        /// <c>Data\Biotech\Defs\ThingDefs_Items\Items_Resource_Manufactured.xml</c>）。
        /// </summary>
        public static bool IsHazardousWaste(ThingDef def)
        {
            if (def?.comps == null) return false;

            for (int i = 0; i < def.comps.Count; i++)
            {
                if (def.comps[i] is CompProperties_DissolutionEffectPollution) return true;
                if (def.comps[i] is CompProperties_Dissolution) return true;
            }
            return false;
        }

        public static bool IsHazardousWaste(Thing t)
        {
            return t != null && IsHazardousWaste(t.def);
        }
    }
}
