using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// <b>面板里的一条"bill"</b> —— 配方**不再镜像工作台上已有的 bill**，而是玩家在我们自己的面板里选的
    /// （工作台只负责"解锁配方"）。
    ///
    /// <para>三种模式，对应原版 <c>BillRepeatModeDef</c>：
    /// <list type="bullet">
    /// <item><b>无限</b>（<see cref="ModeForever"/>）：一直做。</item>
    /// <item><b>次数</b>（<see cref="ModeCount"/>）：做满 <see cref="remaining"/> 件就停（做完则 <see cref="Done"/>）。</item>
    /// <item><b>维持数量</b>（<see cref="ModeTarget"/>）：照抄原版 TargetCount 语义 ——
    ///   产物总数低于 <see cref="targetCount"/> 就做，达标后停；<see cref="pauseWhenSatisfied"/> 打开时
    ///   会"达标即暂停、掉到 <see cref="ResumeAt"/> 才恢复"。计数用原版
    ///   <c>recipe.WorkerCounter.CountProducts</c>（它自己就扫 haul source ⇒ 核心里的产物天然算进去）。</item>
    /// </list></para>
    ///
    /// <para><b>为什么自己存这几个字段</b>：原版 <c>BillStack</c> 必须挂在一个 <c>IBillGiver</c> 上
    /// （<c>Bill.Map</c> / <c>DeletedOrDereferenced</c> 都要 <c>billStack.billGiver</c>），
    /// 而我们的控制面是建筑上的 comp，没有"自己的 BillStack 宿主"。</para>
    /// </summary>
    internal sealed class CraftPlan : IExposable
    {
        /// <summary>一直做。</summary>
        public const int ModeForever = 0;

        /// <summary>做满 N 件就停。</summary>
        public const int ModeCount = 1;

        /// <summary>维持数量（原版 TargetCount）。</summary>
        public const int ModeTarget = 2;

        public RecipeDef recipe;

        /// <summary>
        /// 这条订单<b>限定的材料</b>（null = 不限定，核心里有啥用啥）。
        /// 界面入口：右键订单行。生效点：落在那条临时账单的 <c>ingredientFilter</c> 上
        /// （见 <see cref="BillProbe"/> 的 <c>ApplyMaterial</c>）。
        /// </summary>
        public ThingDef allowedStuff;

        /// <summary>
        /// 这条订单<b>限定的风格</b>（null = 交给虚拟工人的文化自动决定）。
        /// 生效点：<c>Bill.style</c>；原版 <c>GenRecipe.PostProcessProduct</c> 会把它落到产物上。
        /// </summary>
        public ThingStyleDef styleDef;

        /// <summary>
        /// ★ 第 2 步：这条订单是不是**合成 Job 建出来的**。
        /// 取消 Job 时只剪"Job 建出来的、且没有别的 Job 还要"的订单 ⇒ 玩家自己加的订单永不被误删。
        /// （老存档读不到 = false = 玩家自己的订单，行为与改之前完全一致。）
        /// </summary>
        public bool FromJob;

        public int mode = ModeForever;

        /// <summary>次数模式下的剩余件数。</summary>
        public int remaining;

        /// <summary>维持数量模式下的目标数量。</summary>
        public int targetCount = 10;

        /// <summary>维持数量模式：达标后暂停，掉到 <see cref="ResumeAt"/> 才恢复（原版同名语义）。</summary>
        public bool pauseWhenSatisfied;

        /// <summary>维持数量模式：当前是否处于"达标暂停"。</summary>
        public bool paused;

        public bool suspended;

        /// <summary>这个配方累计做出来多少件（面板显示用）。</summary>
        public int completed;

        // ---- 运行时（不进存档）----

        /// <summary>当前在产的生产线（每条 = 一张台子）。读档后按可用台子重建。</summary>
        public readonly List<CraftLine> lines = new List<CraftLine>();

        /// <summary>维持数量模式：最近一次数到的产物总数。</summary>
        public int countedCount;

        /// <summary>维持数量模式：上次计数的 tick（0 = 需要重数）。</summary>
        public int countedTick;

        /// <summary>维持数量模式：这一轮**要不要开工**（低于目标且没暂停）。</summary>
        public bool wantsWork = true;

        /// <summary>用于计数的临时原版 bill（挂在某张能做该配方的台子上；不需要它真的在生产）。</summary>
        public Bill_Production CountBill;

        /// <summary>计数 bill 的宿主栈。</summary>
        public BillStack CountStack;

        /// <summary>次数模式做完了。</summary>
        public bool Done
        {
            get { return mode == ModeCount && remaining <= 0; }
        }

        /// <summary>
        /// 这个订单还要不要**保留生产线**（挂起/做完了就不要了）。
        /// 注意与 <see cref="wantsWork"/> 的区别：维持数量模式"达标暂停"时**在产的那几件要让它做完**
        /// （原版的 paused 也只挡"开始新活"，不打断已开工的），所以仍然保留线。
        /// </summary>
        public bool Maintained
        {
            get { return !suspended && !Done && recipe != null; }
        }

        /// <summary>这一轮能不能**开新活**（维持数量模式随计数变化；次数/无限只看还有没有额度）。</summary>
        public bool CanStartNewWork
        {
            get
            {
                if (!Maintained) return false;
                if (mode == ModeTarget) return wantsWork;
                return true;
            }
        }

        /// <summary>
        /// 达标暂停后，产物掉到目标的 60% 就自动恢复（原版有一个"低于多少恢复"的数字框，
        /// 我们用一个够用的固定比例，少一个 UI 控件；<c>pauseWhenSatisfied</c> 关掉时无滞后）。
        /// </summary>
        public int ResumeAt
        {
            get { return Mathf.Max(0, targetCount - Mathf.Max(1, targetCount * 2 / 5)); }
        }

        /// <summary>玩家点 +N：次数模式加剩余，维持数量模式加目标。</summary>
        public void AddCount(int n)
        {
            if (mode == ModeTarget)
            {
                targetCount = Mathf.Max(0, targetCount + n);
                paused = false;
                wantsWork = true;
                return;
            }
            if (mode != ModeCount)
            {
                mode = ModeCount;
                remaining = 0;
            }
            remaining += n;
            if (remaining < 0) remaining = 0;
            suspended = false;
        }

        public void SetForever()
        {
            mode = ModeForever;
            remaining = 0;
            suspended = false;
            wantsWork = true;
        }

        public void SetTarget(int target)
        {
            mode = ModeTarget;
            targetCount = Mathf.Max(0, target);
            suspended = false;
            paused = false;
            wantsWork = true;
            countedTick = 0;      // 强制重数
        }

        /// <summary>
        /// 照抄 <c>Bill_Production.ShouldDoNow</c> 的 TargetCount 分支（<c>Bill_Production.cs:225-240</c>）：
        /// 更新 <see cref="paused"/>，返回"还要不要做"。
        /// </summary>
        public bool UpdateTargetState(int counted)
        {
            if (mode != ModeTarget) return true;

            if (pauseWhenSatisfied && counted >= targetCount) paused = true;
            if (counted <= ResumeAt || !pauseWhenSatisfied) paused = false;
            if (paused) return false;
            return counted < targetCount;
        }

        /// <summary>做完一件的记账（**唯一入口**，别在别处改 remaining / 计数缓存）。</summary>
        public void NoteCompleted()
        {
            completed++;
            if (mode == ModeCount && remaining > 0) remaining--;
            countedTick = 0;      // 产物数变了 ⇒ 下次重新数（维持数量模式靠它决定停不停）
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref recipe, "recipe");
            Scribe_Values.Look(ref mode, "mode", ModeForever);
            Scribe_Values.Look(ref remaining, "remaining", 0);
            Scribe_Values.Look(ref targetCount, "targetCount", 10);
            Scribe_Values.Look(ref pauseWhenSatisfied, "pauseWhenSatisfied", false);
            Scribe_Values.Look(ref paused, "paused", false);
            Scribe_Values.Look(ref suspended, "suspended", false);
            Scribe_Values.Look(ref completed, "completed", 0);
            // ★ 我们加的字段。读不到就是 null = 原版行为 ⇒ 老存档照样能读；
            //   反过来，带这两个字段的存档给回原版 mod 也只是多两个被忽略的节点，不损坏存档。
            Scribe_Defs.Look(ref allowedStuff, "allowedStuff");
            Scribe_Defs.Look(ref styleDef, "styleDef");
            Scribe_Values.Look(ref FromJob, "fromJob", false);   // ★ 第 2 步：Job 归属标记
            // lines / CountBill / counted* 刻意不存：都是可重建的派生状态
            //（扣料只在完成那一刻 ⇒ 读档丢进度不丢料）。
        }
    }
}
