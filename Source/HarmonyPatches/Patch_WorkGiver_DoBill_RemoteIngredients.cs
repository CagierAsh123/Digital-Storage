using System;
using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>让 b 图上的工作台用得起 a 图核心里的原料</b>（4.0 第三阶段 · 跨图 · 发现层）。
    ///
    /// <para><b>原版为什么看不见跨图原料</b>：<c>WorkGiver_DoBill.TryFindBestIngredientsHelper</c>
    /// 的候选来源只有两处，两处都是**本图**的：</para>
    /// <list type="number">
    /// <item><c>pawn.Map.haulDestinationManager.AllHaulSourcesListForReading</c>（:481）—— 本图的容器表；</item>
    /// <item>区域遍历 <c>r.ListerThings</c>（:531）—— 本图已 Spawned 的散落物。</item>
    /// </list>
    /// <para>另有第三道闸门 <c>pawn.CanReserve(内容物)</c>（:490）—— 见下。</para>
    ///
    /// <para><b>为什么不去"修"那三处，而是挂 <c>foundAllIngredientsAndChoose</c> 这个委托参数</b>：
    /// 前两处是**中间**的循环，Harmony 没有合法插入点；第三处（预订闸门）在
    /// <c>ReservationManager.CanReserve:172</c>，是全局最热的判定之一，为它加前缀不划算
    /// （而且原版那条 <c>MapHeld != map</c> 的判断本身是"别隔图搬东西"的正当语义）。
    /// 委托参数是唯一既能注入候选、又能保证**零回归**的挂点：</para>
    ///
    /// <code>
    /// found = list =&gt; {
    ///     if (original(list)) return true;   // ① 本图解得了 ⇒ 一个字都不改
    ///     appendRemote(list);                // ② 本图解不了 ⇒ 追加其它图的核心内容物
    ///     return original(list);             //    再让**原版自己的选料器**挑一遍
    /// };
    /// </code>
    ///
    /// <para>选料仍然完全由原版 <c>TryFindBestBillIngredientsInSet</c> 完成 —— 本 mod 不发明
    /// 任何挑选规则，也就不会和原版漂移。因为 ① 的存在，本图有货时跨图那段代码一次都不跑。</para>
    ///
    /// <para><b>下游怎么把料拿到手</b>：跨图的原料进了 <c>job.targetQueueB</c> 之后，
    /// <c>JobDriver_DoBill.CollectIngredientsToils</c> 的 <c>GotoThing(canGotoSpawnedParent: true)</c>
    /// 会把目标解析成**另一张图上的核心建筑**，然后照本图坐标走过去。挡住它的是
    /// <c>Backpack/Patch_JobDriver_DoBill_LoadIngredients</c>：它在这条 toil **之前**把料挪进背包，
    /// 于是 <c>SpawnedParentOrMe</c> = 小人自己 ⇒ 0 距离，原版后续 toil 一字不改。
    /// 也就是说"送过去直走背包"这套**本来就与地图无关**，跨图缺的只有发现层。</para>
    ///
    /// <para><b>为什么不顺带覆盖 <c>TryFindBestFixedIngredients</c></b>：它只有
    /// <c>CompBiosculpterPod</c> 一个调用者，拿到 <c>chosenExtraItems</c> 后走的是生物塑型舱自己的
    /// 搬运链（<b>没有</b>背包那一跳）⇒ 跨图原料会让小人走向同名坐标。所以用
    /// <see cref="Patch_WorkGiver_DoBill_BillSearchScope"/> 把范围钉死在"bill 找料"这一条链上。</para>
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_DoBill), "TryFindBestIngredientsHelper")]
    internal static class Patch_WorkGiver_DoBill_RemoteIngredients
    {
        private static void Prefix(Pawn pawn, Predicate<Thing> thingValidator,
            ref Predicate<List<Thing>> foundAllIngredientsAndChoose)
        {
            if (!Patch_WorkGiver_DoBill_BillSearchScope.InBillSearch) return;

            Predicate<List<Thing>> original = foundAllIngredientsAndChoose;
            if (original == null || pawn == null || pawn.Map == null) return;

            // 单图（绝大多数存档）：一次 Count 比较就退出，零开销
            List<Map> maps = Find.Maps;
            if (maps == null || maps.Count <= 1) return;

            bool remoteTried = false;
            foundAllIngredientsAndChoose = delegate (List<Thing> found)
            {
                if (found == null) return original(found);

                // ① 本图先解。解得了就完全不碰跨图 —— 这是"本图有货时零行为变化"的保证。
                if (original(found)) return true;

                // ② 本图凑不齐。同一个 <c>relevantThings</c> 会被反复传进来，
                //    追加只做一次（否则会重复计数，选出超过实际库存的量）。
                if (remoteTried) return false;
                remoteTried = true;

                int added = RemoteIngredientProvider.Append(found, pawn, thingValidator);
                if (added <= 0) return false;

                if (DigitalStorage.Settings.DigitalStorageSettings.enableDebugLog)
                    Log.Warning("[DS] 跨图取料：本图凑不齐，从其它地图的核心追加 " + added + " 件候选原料");

                return original(found);
            };
        }
    }

    /// <summary>
    /// 标出"当前这次搜索是 bill 找料"。<c>TryFindBestIngredientsHelper</c> 是
    /// <c>TryFindBestBillIngredients</c> 与 <c>TryFindBestFixedIngredients</c> 共用的私有方法，
    /// 参数里没有 <c>Bill</c>/<c>Recipe</c> 可以区分，只能由外层入口打标记。
    ///
    /// <para>把标记限定在当前调用线程；退出时恢复进入前的值，兼容嵌套搜索。
    /// 异常路径也必须恢复，所以使用 Finalizer，而不是只在正常返回时运行的 Postfix。</para>
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_DoBill), "TryFindBestBillIngredients")]
    internal static class Patch_WorkGiver_DoBill_BillSearchScope
    {
        [ThreadStatic]
        internal static bool InBillSearch;

        private static void Prefix(out bool? __state)
        {
            __state = InBillSearch;
            InBillSearch = true;
        }

        private static Exception Finalizer(Exception __exception, bool? __state)
        {
            // 其它 Prefix 提前跳过本 Prefix 时，__state 为 null，不接管外层状态。
            if (__state.HasValue) InBillSearch = __state.Value;
            return __exception;
        }
    }

    /// <summary>
    /// 同一条链上的**另一个**入口：<c>TryFindBestFixedIngredients</c>（只有 <c>CompBiosculpterPod</c> 用）。
    /// 它同样会走 <c>TryFindBestIngredientsHelper</c>，执行期间必须关闭 bill 标记。
    ///
    /// <para>固定原料搜索不应拿到跨图候选：生物塑型舱的搬运链没有背包取料步骤。
    /// 退出时恢复之前的标记，避免嵌套调用把外层 bill 搜索的状态永久清掉。
    /// Finalizer 原样返回异常，不吞掉上游错误。</para>
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_DoBill), "TryFindBestFixedIngredients")]
    internal static class Patch_WorkGiver_DoBill_FixedSearchScope
    {
        private static void Prefix(out bool? __state)
        {
            __state = Patch_WorkGiver_DoBill_BillSearchScope.InBillSearch;
            Patch_WorkGiver_DoBill_BillSearchScope.InBillSearch = false;
        }

        private static Exception Finalizer(Exception __exception, bool? __state)
        {
            if (__state.HasValue)
                Patch_WorkGiver_DoBill_BillSearchScope.InBillSearch = __state.Value;
            return __exception;
        }
    }

    /// <summary>
    /// 跨图原料的候选提供者。
    /// </summary>
    internal static class RemoteIngredientProvider
    {
        /// <summary>
        /// 把**其它地图**上核心的**直接**内容物里可用作本次原料的东西追加进
        /// <paramref name="found"/>，返回追加的件数。
        ///
        /// <para><b>为什么只取直接内容物（不递归）</b>：<c>WorkGiver_DoBill:487</c> 对本图容器
        /// 用的是 <c>GetAllThingsRecursively</c>。跨图这边不能跟着递归 —— 递归出来的东西
        /// （尸体里的 pawn、压缩建筑里的建筑）<c>ParentHolder</c> 不是核心，
        /// 背包取料 toil 的 <c>t.ParentHolder is Building_StorageCore</c> 判定会跳过它们，
        /// 于是队列里留下一个指向**另一张图**的目标，小人会照本图坐标走过去。
        /// 只收直接内容物，则"能被挑中"与"能被吸进背包"是同一个集合。</para>
        ///
        /// <para><b>禁止判定用 <c>IsForbidden(pawn.Faction)</c> 而不是 <c>IsForbidden(pawn)</c></b>：
        /// 后者内部有一句 <c>t.PositionHeld.IsForbidden(pawn)</c> ⇒ 拿 a 图的坐标去查
        /// b 图的活动区（<c>ForbidUtility.cs:117</c>），结论纯属噪声。</para>
        /// </summary>
        internal static int Append(List<Thing> found, Pawn pawn, Predicate<Thing> validator)
        {
            if (found == null || pawn == null || validator == null || pawn.Map == null) return 0;

            int added = 0;
            List<Map> maps = Find.Maps;
            if (maps == null) return 0;

            for (int i = 0; i < maps.Count; i++)
            {
                Map map = maps[i];
                if (map == null || map == pawn.Map) continue;

                List<Building_StorageCore> cores = CoreFinder.AllUsableCores(map);
                for (int c = 0; c < cores.Count; c++)
                {
                    ThingOwner held = cores[c].GetDirectlyHeldThings();
                    if (held == null) continue;

                    for (int k = 0; k < held.Count; k++)
                    {
                        Thing t = held[k];
                        if (t == null || t.Destroyed) continue;
                        if (!validator(t)) continue;
                        if (t.IsForbidden(pawn.Faction)) continue;

                        found.Add(t);
                        added++;
                    }
                }
            }
            return added;
        }
    }
}
