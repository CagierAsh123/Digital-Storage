using DigitalStorage.Components;
using HarmonyLib;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>物品每次落地，都重新起算"别碰"窗口</b>（用户 2026-10-23 反复上报的
    /// 「小人偶尔会把东西取出来又塞回系统」「取出→塞回→再取出」）。
    ///
    /// <para><b>旧窗口为什么不够用</b>：自动收纳的两笔避让窗口都从<b>取出那一刻</b>起算，
    /// 而且都是 300 tick（≈5 秒，一个 job 周期的量级）：</para>
    /// <list type="bullet">
    /// <item><c>CompAutoIngest.withdrawnUntil</c>：<c>MarkWithdrawn</c> 在"从小人手上/核心离开"时
    /// 盖 300 tick 戳（见 <see cref="Patch_CarryFromCore_MarkWithdrawn"/>）；</item>
    /// <item><c>CompAutoIngest.rimFirstSeen</c>：躺在"还有 bill 在等料"的工作台旁边时的宽限，
    /// 只从**首次**看见起算，过期不再续。</item>
    /// </list>
    ///
    /// <para>问题在于：料被取出后还要**走一段路、再放下来**，而落地之后才真正开始被使用。
    /// 只要从取出到落地超过 300 tick（远距离工地、跨图、搬运工排队、密集路径），
    /// 落地这一 tick 窗口已经过期 ⇒ 15 tick 内被自动收纳瞬移回核心 ⇒ 那个作业找不到料、
    /// 只能重新取一趟 ⇒ 玩家看到的就是"来回搬"。工作台旁边那一圈同理：
    /// 第二次把料放下来时首见时刻早已过期，宽限完全失效。</para>
    ///
    /// <para>所以挂 <c>Thing.SpawnSetup</c>（唯一"物品出现在地图上"的入口，<c>Toils_Haul.TryDropCarriedThing</c>、
    /// <c>GenPlace.TryPlaceThing</c>、<c>ThingOwner.TryDrop</c> 全部经过它）：
    /// 只对<b>近期确实被取出过</b>的物品重算窗口（<see cref="CompAutoIngest.TryRearmAfterSpawn"/>），
    /// 普通搬运（从地上捡起再放下）不会因此获得免疫。</para>
    ///
    /// <para>成本：每件物品每次落地一次字典查找（两个 <c>Dictionary.TryGetValue</c>），
    /// 只对 <c>ThingCategory.Item</c> 生效 —— 建筑、地形、Pawn 全部直接跳过。</para>
    /// </summary>
    [HarmonyPatch(typeof(Thing), nameof(Thing.SpawnSetup))]
    internal static class Patch_Thing_SpawnSetup_RearmWithdrawn
    {
        private static void Postfix(Thing __instance)
        {
            if (__instance == null || __instance.def == null) return;
            if (__instance.def.category != ThingCategory.Item) return;
            CompAutoIngest.TryRearmAfterSpawn(__instance);
        }
    }
}
