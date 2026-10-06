// =====================================================================================
//  【本地新增文件】Digital Storage 本地整合版 —— 上游 main 分支**没有**这个文件。
// -------------------------------------------------------------------------------------
//  本文件是什么：
//    **合成 Job** —— 照 AE2（AE2-UEL）的 `ExecutingCraftingJob` 做的模型：
//    **一个合成请求 = 一个 Job = 一整棵依赖树**，而不是"一堆互不相干的订单"。
//
//  为什么必须有它（用户实机报的 bug）：
//    "我发了一个订单，但是我把最终产物删除了，下游产物订单没有取消"
//    —— 现在下游订单是**并列的 CraftPlan**，彼此不知道谁依赖谁 ⇒ 结构上就做不到"删父连带删子"。
//    AE2 里这件事**结构上不可能发生**：CPU 只有一张 `Map<IPatternDetails, TaskProgress>`，
//    取消就是 `job = null` 整表丢弃（见 DS-AE2合成逻辑改造规格.md §6.1 的反汇编证据）。
//
//  和 exec 层的关系（重要）：
//    `CraftPlan` / `CraftLine` **完全不动**，仍然是执行层（每条配方一条 plan、每条产线一张台子）。
//    CraftJob 只在它上面加一层"归属 + 依赖 + 取消传播"：
//      · 每个 step 记下自己的**深度**（0 = 根，越大越上游）⇒ 分配台子时上游优先；
//      · 每个 step 的"做完没有"**不自己记账**，而是读那条 plan 的现成状态
//        （维持模式达标 / 次数模式做完）⇒ 不会出现"两处各记一套、然后漂移"。
//
//  怎么回退：删掉本文件 + 对应那一组改动（source-changes-groupAI.ps1）即可。
// =====================================================================================
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 合成 Job 的状态（AE2 的 <c>CraftingJobStatusPacket.Status</c> 是 STARTED / CANCELLED / FINISHED，
    /// 我们多一个"进行中"的值当默认态，语义一致）。
    /// </summary>
    internal static class CraftJobState
    {
        /// <summary>进行中（AE2 的 STARTED）。</summary>
        public const int Crafting = 0;

        /// <summary>根产物已经达标 ⇒ 完成（AE2 的 FINISHED）。</summary>
        public const int Finished = 1;

        /// <summary>被玩家取消（AE2 的 CANCELLED）。</summary>
        public const int Cancelled = 2;
    }

    /// <summary>
    /// <b>一次合成请求</b>（AE2 的 <c>CraftingRequest</c> + <c>ExecutingCraftingJob</c> 的合体）。
    ///
    /// <para>持有一整棵依赖树（<see cref="steps"/>），所以"取消 = 整棵树一起销毁"是**结构上**成立的，
    /// 不需要额外去找谁依赖谁。完成判据只看**根步骤**（AE2 也是：最终产物交付完 ⇒ <c>finishJob(true)</c>，
    /// 剩下的子任务跟着一起丢）。</para>
    /// </summary>
    internal sealed class CraftJob : IExposable
    {
        /// <summary>根配方（玩家点的那一条）。</summary>
        public RecipeDef rootRecipe;

        /// <summary>根产物（可能为 null：specialProducts-only 的配方没有 ProducedThingDef）。</summary>
        public ThingDef rootProduct;

        /// <summary>最终要几个（维持模式的目标）。</summary>
        public int wanted = 1;

        /// <summary><see cref="CraftJobState"/> 之一。</summary>
        public int state = CraftJobState.Crafting;

        public int createdTick;

        /// <summary>结束（完成 / 取消）的 tick；-1 = 还没结束。</summary>
        public int finishedTick = -1;

        /// <summary>依赖树的全部步骤，<c>[0]</c> 一定是根。</summary>
        public List<CraftJobStep> steps = new List<CraftJobStep>();

        public bool Active
        {
            get { return state == CraftJobState.Crafting; }
        }

        public CraftJobStep Root
        {
            get { return (steps.Count > 0) ? steps[0] : null; }
        }

        /// <summary>树有多深（界面显示"N 层"用）。</summary>
        public int MaxDepth
        {
            get
            {
                int d = 0;
                for (int i = 0; i < steps.Count; i++)
                {
                    if (steps[i] != null && steps[i].depth > d) d = steps[i].depth;
                }
                return d;
            }
        }

        /// <summary>这个 Job 需不需要该配方（取消时用来判断"哪个 plan 还能留"）。</summary>
        public int IndexOfRecipe(RecipeDef r)
        {
            if (r == null) return -1;
            for (int i = 0; i < steps.Count; i++)
            {
                if (steps[i] != null && steps[i].recipe == r) return i;
            }
            return -1;
        }

        /// <summary>已经做了几次（只统计根步骤那条 plan 的完成数，界面用）。</summary>
        public void ExposeData()
        {
            Scribe_Defs.Look(ref rootRecipe, "rootRecipe");
            Scribe_Defs.Look(ref rootProduct, "rootProduct");
            Scribe_Values.Look(ref wanted, "wanted", 1);
            Scribe_Values.Look(ref state, "state", CraftJobState.Crafting);
            Scribe_Values.Look(ref createdTick, "createdTick", 0);
            Scribe_Values.Look(ref finishedTick, "finishedTick", -1);
            Scribe_Collections.Look(ref steps, "steps", LookMode.Deep);

            if (Scribe.mode != LoadSaveMode.PostLoadInit) return;

            if (steps == null) steps = new List<CraftJobStep>();
            // 配方 def 被删（换 mod / 换版本）⇒ 安静丢掉那一步，别让界面里出现空行
            for (int i = steps.Count - 1; i >= 0; i--)
            {
                if (steps[i] == null || steps[i].recipe == null) steps.RemoveAt(i);
            }
            // ★ children 里存的是**下标**，删过步骤之后会越界 ⇒ 按 parent 重建一遍（单一数据源）。
            for (int i = 0; i < steps.Count; i++)
            {
                CraftJobStep s = steps[i];
                if (s.children == null) s.children = new List<int>();
                else s.children.Clear();
                if (s.parent >= steps.Count || s.parent == i) s.parent = -1;
            }
            for (int i = 1; i < steps.Count; i++)
            {
                int p = steps[i].parent;
                if (p >= 0 && p < steps.Count) steps[p].children.Add(i);
            }
        }
    }

    /// <summary>
    /// 依赖树里的一个加工步骤（AE2 的 <c>tasks</c> 表里的一项 =
    /// "这个样板还要做几次"）。DS 这边一个步骤 = 一条配方 = 一条 <see cref="CraftPlan"/>。
    /// </summary>
    internal sealed class CraftJobStep : IExposable
    {
        public RecipeDef recipe;

        /// <summary>这一步做出来的产物（界面显示用）。</summary>
        public ThingDef product;

        /// <summary>需要产出多少**件**（= 核心里要维持的量；取自 <c>CraftTreeNode.RequiredTotal</c>）。</summary>
        public int needCount = 1;

        /// <summary>换算成"做几次"（= <c>CraftTreeNode.Crafts</c>，界面显示用）。</summary>
        public int crafts = 1;

        /// <summary>0 = 根；越大越靠上游（分配工作台时上游优先）。</summary>
        public int depth;

        /// <summary>谁需要我（<see cref="CraftJob.steps"/> 的下标）；-1 = 根。</summary>
        public int parent = -1;

        /// <summary>我需要谁（下标表）。</summary>
        public List<int> children = new List<int>();

        public bool IsRoot
        {
            get { return parent < 0; }
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref recipe, "recipe");
            Scribe_Defs.Look(ref product, "product");
            Scribe_Values.Look(ref needCount, "needCount", 1);
            Scribe_Values.Look(ref crafts, "crafts", 1);
            Scribe_Values.Look(ref depth, "depth", 0);
            Scribe_Values.Look(ref parent, "parent", -1);
            // ★ children **刻意不写存档**：它完全可以从 parent 推出来（下面 CraftJob.ExposeData 里重建），
            //   少一份可能不一致的冗余数据，也避开"用下标存引用"在删步骤后越界的坑。
            if (Scribe.mode == LoadSaveMode.PostLoadInit && children == null) children = new List<int>();
        }
    }
}
