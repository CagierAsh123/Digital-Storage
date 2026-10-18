using DigitalStorage.Effects;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 一次结算的结果。
    ///
    /// <para><b>为什么要三态而不是 bool</b>：<see cref="BillCraftFunnel.TryComplete"/> 失败有两种完全不同的原因 ——
    /// "这次作废"（配方被挂起/做完、台子断电、原料被抢）应当**丢掉进度重新取活**；
    /// 而"这一 tick 拿不到完成预算"只是限流，活其实已经干完了，**必须保留进度**等下一 tick 的票。
    /// 用 bool 就会把后者降级成"重做一遍"，在预算吃紧时表现为产量凭空掉一半。</para>
    /// </summary>
    internal enum CraftResult
    {
        Done,
        NoBudget,
        Invalid
    }

    /// <summary>
    /// <b>一条在产的生产线 = 一张工作台 + 这个配方这一轮的进度。</b>
    ///
    /// <para>它替代了旧结构里的 <c>BillSlot</c>（那时一个槽位对应"台子上已有的 bill"）。
    /// 现在台子上不需要有任何 bill：<see cref="Bill"/> 是**我们自己造的**原版 bill，
    /// 只为借原版选料器用一次（见 <see cref="BillProbe"/> / <see cref="BillStackSwap"/>）。</para>
    ///
    /// <para>进度与原料都是派生状态（不进存档）；扣料只发生在完成那一刻。</para>
    /// </summary>
    internal sealed class CraftLine
    {
        public CraftPlan Plan;

        /// <summary>这条线借用的工作台（一定实现了 <c>IBillGiver</c>）。</summary>
        public Thing Bench;

        /// <summary>我们自己的原版 bill 对象，宿主是 <see cref="TempStack"/>。选料时临时换进台子。</summary>
        public Bill_Production Bill;

        /// <summary><see cref="Bill"/> 的宿主栈（只为了让 <c>bill.billStack</c> 非空）。</summary>
        public BillStack TempStack;

        /// <summary>原版 <c>WorkGiver_DoBill.JobOnThing</c> 给的 job：只取里面的 bill + 原料，job 从不上岗。</summary>
        public Job Probe;

        public Thing[] Ingredients;
        public int[] Counts;

        /// <summary>原版速度公式在"取活那一刻"的值：<c>workSpeedStat</c> × 台子 <c>workTableSpeedStat</c>。</summary>
        public float BaseRate = 1f;

        public float WorkAmount;
        public float WorkLeft;

        public int NextAcquireTick;

        /// <summary>没在干活时的原因（Keyed 键名）；null = 正在干活。</summary>
        public string BlockKey;

        // ---- 表现件（手 / 黄条）----
        public Mote_DS_WorkHand Hand;
        public Effecter Bar;
        private float strikeTimer;

        public bool HasWork
        {
            get { return Bill != null && Ingredients != null; }
        }

        public float Progress01
        {
            get
            {
                if (WorkAmount <= 0f) return 1f;
                return Mathf.Clamp01(1f - WorkLeft / WorkAmount);
            }
        }

        /// <summary>丢掉这一轮（保留线与台子认领）。</summary>
        public void ClearWork()
        {
            // B1：这一轮不做了 ⇒ 立刻把原料认领退掉，别让别的线干等（StillValid 失败、
            // 产出结算、线释放都会走到这里，是唯一的收口点）。
            if (Ingredients != null) CraftIngredientClaims.Release(this);
            Probe = null;
            Ingredients = null;
            Counts = null;
            BaseRate = 1f;
            WorkAmount = 0f;
            WorkLeft = 0f;
        }

        /// <summary>彻底释放这条线（连带表现件）。</summary>
        public void ClearAll()
        {
            ClearWork();
            CleanupVisual();
            Bench = null;
            Bill = null;
            TempStack = null;
            Plan = null;
            NextAcquireTick = 0;
            BlockKey = null;
        }

        // ===================================================================
        // 表现：工作台上那只手 + 底下的黄色读条
        // ===================================================================

        /// <summary>与 <c>CompDigitalWorker.UpdateVisuals</c> 同构；<c>index &gt;= cap</c> 时收掉（防"手海"）。</summary>
        public void TickVisual(Map map, int index, int cap)
        {
            Thing t = Bench;
            if (index >= cap || t == null || t.Destroyed || !t.Spawned || !HasWork)
            {
                CleanupVisual();
                return;
            }

            if (Hand == null || Hand.Destroyed || !Hand.Spawned)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail("DS_WorkHand");
                if (def != null)
                {
                    Mote_DS_WorkHand m = ThingMaker.MakeThing(def) as Mote_DS_WorkHand;
                    if (m != null)
                    {
                        GenSpawn.Spawn(m, t.PositionHeld, map);
                        Hand = m;
                    }
                }
            }
            if (Hand != null && !Hand.Destroyed)
            {
                Vector3 pos = t.DrawPos;
                pos.y = 0f;        // y 由 Mote.DrawMote 按 altitudeLayer 每帧重设
                pos.z += 0.15f;    // 略微朝镜头，压在目标正面
                Hand.exactPosition = pos;
                Hand.Maintain();   // 不再 Maintain 时它 1 秒后自愈消失

                strikeTimer -= 1f;
                if (strikeTimer <= 0f)
                {
                    strikeTimer = 30f;   // 半秒挥一下
                    Hand.Strike();
                }
            }

            if (Bar == null) Bar = EffecterDefOf.ProgressBar.Spawn();
            Bar.EffectTick(new TargetInfo(t), TargetInfo.Invalid);

            MoteProgressBar mote = (Bar.children.Count > 0)
                ? (Bar.children[0] as SubEffecter_ProgressBar)?.mote
                : null;
            if (mote != null)
            {
                mote.progress = Progress01;
                mote.offsetZ = -0.5f;     // 原版 WithProgressBar 的默认位置（贴在目标"底下"）
                mote.alwaysShow = true;   // 代理可能在远离镜头处干活，别只在最近缩放才画
            }
        }

        public void CleanupVisual()
        {
            if (Hand != null && !Hand.Destroyed) Hand.Destroy();
            Hand = null;
            if (Bar != null)
            {
                Bar.Cleanup();
                Bar = null;
            }
            strikeTimer = 0f;
        }
    }
}
