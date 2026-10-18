using RimWorld;
using Verse;

namespace DigitalStorage.Core
{
    /// <summary>
    /// 「有害垃圾」判定：有毒垃圾 / 废料包这一族（<c>ThingDefOf.Wastepack</c> 及其 mod 变体）。
    ///
    /// <para><b>为什么要单列一类</b>：这类物品的清理链是
    /// <c>WorkGiver_EmptyWasteContainer</c> → <c>JobDriver_EmptyThingContainer</c>，而那条 driver
    /// <b>只支持格子目的地</b>（收尾是 <c>CarryHauledThingToCell</c> + <c>PlaceHauledThingInCell</c>）。
    /// 只要"最优目的地"是容器（例如高优先级的存储核心），原版就会把
    /// <c>foundCell = IntVec3.Invalid</c> 塞进 <c>job.targetC</c>，而
    /// <c>JobDriver_EmptyThingContainer.TryMakePreToilReservations</c> 会预订这个非法格子并失败
    /// ⇒ 作业永远建不起来、也没有任何报错（玩家只看到"小人就是不来清理"）。</para>
    ///
    /// <para><b>现在的做法</b>（2026-10-18 下午，社区反馈后反转）：核心<b>照收</b>有害垃圾，
    /// 清空链由我们自己接管 —— <c>HarmonyPatches/Patch_EmptyWasteContainerFallback</c> 发现
    /// "最优目的地是能收这份垃圾的<b>核心</b>"时，换成
    /// <c>DigitalStorage.AI.JobDriver_DS_EmptyWasteToCore</c>（先把废料散到地上，再抱起来送进核心）。
    /// 于是原版那条只会往格子里放的 driver 根本不会被用在容器目的地上。</para>
    ///
    /// <para>本类在这里的价值：① 判定"这是垃圾"，避免把普通容器清空作业也改道；
    /// ② 给测试/日志一个统一的判据。**筛选器不再用它做默认排除**。</para>
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
