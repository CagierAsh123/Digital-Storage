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
    /// 它不知道"这件东西刚从核心出来"。这里补上：小人开始搬一件东西时，如果它此刻的
    /// <c>ParentHolder</c> 是我们的核心，就给它盖一个 300 tick 的"刚被取出"戳
    /// （<see cref="CompAutoIngest.MarkWithdrawn"/>），落地后不会被立刻吸回去。</para>
    ///
    /// <para><b>为什么挂在 <c>Pawn_CarryTracker.TryStartCarry</c> 而不是 <c>Toils_Haul.StartCarryThing</c></b>：
    /// 后者是"造 toil"的工厂方法，真正决定拿哪件东西是 toil 的 <c>initAction</c> 在运行时做的，
    /// Patch 工厂拿不到运行时的物品。而 <c>TryStartCarry</c> 是**唯一**会把物品从容器搬进手上的入口
    /// （原版 <c>Toils_Haul.cs:77</c> 就是调它），此时 <c>item.ParentHolder</c> 仍然是核心。
    /// 两个重载都要挂：<c>TryStartCarry(Thing)</c> 与 <c>TryStartCarry(Thing, int, bool)</c>。</para>
    ///
    /// <para>只标记**从我们核心拿出来的**那件（<c>ParentHolder as Building_StorageCore</c>）；
    /// 从地上捡、从别家容器拿，一律不干预。与"避让作业"开关无关 —— 这条永远生效：
    /// 它是"别把自己的东西立刻吸回去"的物理常识，不是让位策略。</para>
    /// </summary>
    internal static class Patch_CarryFromCore_MarkWithdrawn
    {
        [HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryStartCarry),
            new[] { typeof(Thing), typeof(int), typeof(bool) })]
        internal static class ThreeArg
        {
            private static void Postfix(Pawn_CarryTracker __instance, Thing item, int count, bool reserve,
                ref int __result)
            {
                if (__result <= 0) return;
                MarkIfFromCore(__instance, item);
            }
        }

        [HarmonyPatch(typeof(Pawn_CarryTracker), nameof(Pawn_CarryTracker.TryStartCarry), new[] { typeof(Thing) })]
        internal static class OneArg
        {
            private static void Postfix(Pawn_CarryTracker __instance, Thing item, ref bool __result)
            {
                if (!__result) return;
                MarkIfFromCore(__instance, item);
            }
        }

        private static void MarkIfFromCore(Pawn_CarryTracker tracker, Thing source)
        {
            if (tracker == null || source == null) return;
            if (!(source.ParentHolder is Building_StorageCore)) return;

            // ⚠️ 标的是**真正被搬走的那一件**：部分取货时原版会 SplitOff 出一个新实例，
            //    落地的是新实例（新 thingIDNumber），标老的没有用。
            Thing carried = tracker.CarriedThing;
            if (carried != null && !carried.Destroyed) CompAutoIngest.MarkWithdrawn(carried);
            else if (!source.Destroyed) CompAutoIngest.MarkWithdrawn(source);
        }
    }
}
