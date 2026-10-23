using DigitalStorage.Components;
using HarmonyLib;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>把"刚从核心拿出来"这件事记到自动收纳的避让表里</b>（社区反馈 2026-10-18 第 3 条：
    /// 「小人会无限从核心搬运食物原材料、核心无限存食物，循环导致非常卡」）。
    ///
    /// <para><b>死循环怎么成的</b>：原版 bill 取料走
    /// <c>JobDriver_DoBill.cs:121 GotoThing(ingredientInd, ClosestTouch, canGotoSpawnedParent:true)</c>
    /// —— 小人可以直接从**核心**里把原料拿走。而作业一旦被打断 / 被抢 / 配方已达标，
    /// 手上的料会落地（<c>PlaceHauledThingInCell</c> 的 <c>ThingPlaceMode.Near</c> 兜底）。
    /// 本 mod 的自动收纳是 15 tick 内瞬移，于是料**落地即被吸回核心**，下一个取料作业又从核心拿
    /// —— 小人反复搬、核心一直在存、CPU 一直在跑。</para>
    ///
    /// <para><b>我们自己的取出路径早就有护栏</b>（<c>HaulSourceContents.ExtractTo</c> 里的
    /// <c>MarkWithdrawn(result)</c>，300 tick），但**原版取料路径不写这个标记** ——
    /// 它不知道"这件东西刚从核心出来"。这里补上。</para>
    ///
    /// <para><b>为什么挂在 <c>Pawn_CarryTracker.TryStartCarry</c> 而不是 <c>Toils_Haul.StartCarryThing</c></b>：
    /// 后者是"造 toil"的工厂方法，真正决定拿哪件东西是 toil 的 <c>initAction</c> 在运行时做的，
    /// Patch 工厂拿不到运行时的物品。而 <c>TryStartCarry</c> 是**唯一**会把物品从容器搬进手上的入口
    /// （原版 <c>Toils_Haul.cs:77</c> 就是调它）。两个重载都要挂：
    /// <c>TryStartCarry(Thing)</c> 与 <c>TryStartCarry(Thing, int, bool)</c>。</para>
    ///
    /// <para><b>2026-10-23 修正</b>：原来是在 <b>Postfix</b> 里看 <c>source.ParentHolder is Building_StorageCore</c>，
    /// 但 <c>TryStartCarry</c> 内部会先 <c>SplitOff</c>：<b>整堆取走</b>时
    /// <c>Verse\Thing.cs:1604-1618</c> 会 `holdingOwner?.Remove(this)` 再返回 `this`
    /// ⇒ Postfix 里 ParentHolder 早就是 null（或不指向核心）⇒ <b>整堆取料从来没被标记过</b>，
    /// 只有"部分取走"（SplitOff 出新实例、原堆还留在核心）才盖到。
    /// 现在改用 Prefix 的 <c>__state</c> 记录"取之前它是不是在核心里"，取完再盖到
    /// <c>CarriedThing</c> 上（部分=新实例、整堆=同一个实例）。</para>
    /// </summary>
    internal static class Patch_CarryFromCore_MarkWithdrawn
    {
        [HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryStartCarry),
            new[] { typeof(Thing), typeof(int), typeof(bool) })]
        internal static class ThreeArg
        {
            private static void Prefix(Thing item, out bool __state)
            {
                __state = item != null && item.ParentHolder is Building_StorageCore;
            }

            private static void Postfix(Pawn_CarryTracker __instance, bool __state, ref int __result)
            {
                if (__result <= 0 || !__state) return;
                MarkCarried(__instance);
            }
        }

        [HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryStartCarry), new[] { typeof(Thing) })]
        internal static class OneArg
        {
            private static void Prefix(Thing item, out bool __state)
            {
                __state = item != null && item.ParentHolder is Building_StorageCore;
            }

            private static void Postfix(Pawn_CarryTracker __instance, bool __state, ref bool __result)
            {
                if (!__result || !__state) return;
                MarkCarried(__instance);
            }
        }

        /// <summary>
        /// 标的是**真正被搬走的那一件**：部分取货时原版 SplitOff 出一个新实例（新 thingIDNumber），
        /// 落地的是新实例；整堆取货时是同一个实例。两种情况 <c>CarriedThing</c> 都指向它。
        /// </summary>
        private static void MarkCarried(Pawn_CarryTracker tracker)
        {
            if (tracker == null) return;
            Thing carried = tracker.CarriedThing;
            if (carried != null && !carried.Destroyed) CompAutoIngest.MarkWithdrawn(carried);
        }
    }
}
