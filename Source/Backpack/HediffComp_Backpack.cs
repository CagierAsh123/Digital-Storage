using System;
using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Backpack
{
    public class HediffCompProperties_Backpack : HediffCompProperties
    {
        // 每个原料队列目标需要独立堆位
        public int capacityStacks = 8;

        public HediffCompProperties_Backpack()
        {
            compClass = typeof(HediffComp_Backpack);
        }
    }

    /// <summary>
    /// 「背包」：挂在 pawn 身上的**私有** ThingOwner。
    ///
    /// <para><b>它解决什么</b>：原版 bill 取料时，<c>JobDriver_DoBill.cs:121</c> 会先
    /// <c>GotoThing(ingredientInd, ClosestTouch, canGotoSpawnedParent: true)</c>；
    /// 目标取 <c>SpawnedParentOrMe</c>，而核心内容物的它是**核心建筑**（<c>Building_StorageCore</c>）
    /// ⇒ 小人必须先走到核心门口。原料挪进背包后，
    /// <c>SpawnedParentOrMe</c> = <b>小人自己</b>（链条 物品 → 本 comp → Pawn）
    /// ⇒ 这一跳变成 0 距离，原版后续 toil 一字不改。</para>
    ///
    /// <para><b>为什么不做成 haul source</b>（实验版曾注册，现已撤掉）：原版 bill 的容器扫描
    /// <c>WorkGiver_DoBill.cs:483</c> 硬要求
    /// <c>item is Thing { Spawned: not false, Position: var position }</c> —— 本 comp 不是 Thing，
    /// <b>永远进不了原版的取料视野</b>（实测：注册与否对原版行为毫无差别）。
    /// 而注册反而会被本 mod 自己的 <c>HaulSourceContents</c> 收集到 ——
    /// 交易列表 / 资源统计 / 建造选材都会看见背包里的东西。
    /// 现在改由 <c>Patch_JobDriver_DoBill_LoadIngredients</c> 在作业开始时把原料挪进来，
    /// 所以背包不需要、也不应该是 haul source。</para>
    ///
    /// <para><b>为什么不 tick 内容物</b>：没有任何东西调用它的 <c>ThingOwner.DoTick()</c>
    /// （不是 Spawned，也不是 IThingHolderTickable）⇒ 内容物不腐烂、不耗性能。
    /// 背包只是"从核心到工作台"的短途载体，生命周期以秒计。</para>
    ///
    /// <para><b>生命周期</b>：默认空 —— 取料 toil 只在作业开始时按需放入（<see cref="TryAbsorb"/>）；
    /// 每 ~250 tick 检查一次，不在做 bill 就把残留<b>退回核心</b>；
    /// 倒地 / 死亡 / hediff 被移除 ⇒ 先退核心，退不掉才落地，<b>绝不销毁物品</b>。</para>
    /// </summary>
    public class HediffComp_Backpack : HediffComp, IThingHolder
    {
        private ThingOwner backpack;

        public HediffCompProperties_Backpack Props => (HediffCompProperties_Backpack)props;

        // ===================================================================
        // IThingHolder
        // ===================================================================

        /// <summary>
        /// 【地层】链条 <c>物品 → 本 comp → Pawn</c>。
        /// <c>Thing.SpawnedParentOrMe</c> 沿持有者链上爬、**不要求链上是 Thing**
        /// ⇒ 背包内容物的 <c>SpawnedParentOrMe</c> = 小人本人。
        /// 这也是 <c>ErrorCheckForCarry</c>（<c>Toils_Haul.cs:14</c> 要 <c>SpawnedOrAnyParentSpawned</c>）
        /// 与 <c>GotoThing</c>（<c>Toils_Goto.cs:20</c> 运行期取 <c>SpawnedParentOrMe</c>）两处的共同地基。
        /// </summary>
        public IThingHolder ParentHolder => Pawn;

        public ThingOwner GetDirectlyHeldThings()
        {
            if (backpack == null) backpack = new ThingOwner<Thing>(this);
            return backpack;
        }

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, GetDirectlyHeldThings());
        }

        public int Count => GetDirectlyHeldThings().Count;

        // ===================================================================
        // 查找
        // ===================================================================

        /// <summary>pawn 身上的背包；没有返回 null（非玩家阵营 / 未植入 ⇒ 原版流程照走）。</summary>
        public static HediffComp_Backpack For(Pawn pawn)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null) return null;

            List<Hediff> hediffs = pawn.health.hediffSet.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                HediffWithComps withComps = hediffs[i] as HediffWithComps;
                if (withComps == null || withComps.comps == null) continue;

                for (int j = 0; j < withComps.comps.Count; j++)
                {
                    HediffComp_Backpack bag = withComps.comps[j] as HediffComp_Backpack;
                    if (bag != null) return bag;
                }
            }
            return null;
        }

        // ===================================================================
        // 取入 / 退回
        // ===================================================================

        /// <summary>
        /// 把 <paramref name="source"/> 现在所在的容器里的 <paramref name="count"/> 个挪进背包。
        /// 返回**实际进入背包的那件 Thing**（失败返回 null）。
        ///
        /// <para><b>返回值必须是 Thing、不能只是数量</b>：拆堆时进入背包的是
        /// <c>SplitOff</c> **新建的 Thing**，与 <paramref name="source"/> 不是同一个对象。
        /// 调用方（bill 取料 toil）必须拿它去改写作业的队列目标 —— 否则
        /// <c>job.targetQueueB</c> 仍指着核心里原来那一摞，
        /// 原版 <c>GotoThing(..., canGotoSpawnedParent: true)</c> 解析出来还是核心
        /// ⇒ 小人照样走向核心（2026-10-02 实测就是这么走的）。</para>
        ///
        /// <para><b>绝不丢物</b>：每一步失败都原路退回；退回顺序 = 原容器 → 脚下 → 背包（兜底）。</para>
        /// </summary>
        public Thing TryAbsorb(Thing source, int count)
        {
            if (source == null || source.Destroyed || count <= 0) return null;
            Pawn pawn = Pawn;
            if (pawn == null) return null;

            ThingOwner held = GetDirectlyHeldThings();
            if (ReferenceEquals(source.ParentHolder, this)) return source; // 已经在背包里，就是它自己

            // 直接问"装着它的那个 ThingOwner 实例"。Thing.holdingOwner 是 public 字段
            // （Thing.cs:39），而 ThingOwner.Contains 的实现就是 `item.holdingOwner == this`
            // （ThingOwner.cs:609）—— 所以从 ParentHolder 反推 owner 是绕路，还可能推错实例。
            ThingOwner owner = source.holdingOwner;
            if (owner == null || owner.Owner is Map)
            {
                // 地上的东西：holdingOwner 是 Map 自己的容器 ⇒ 不能用（也不该用）
                IThingHolder holder = source.ParentHolder as IThingHolder;
                owner = (holder == null) ? null : holder.GetDirectlyHeldThings();
            }
            if (owner == null || owner.Owner is Map || !owner.Contains(source)) return null;
            Building_StorageCore core = owner.Owner as Building_StorageCore;
            if (core != null && !core.IsUsableNow) return null;

            int want = (count < source.stackCount) ? count : source.stackCount;
            if (want <= 0) return null;
            if (!CanFit(source)) return null;

            Thing taken;
            if (want >= source.stackCount)
            {
                owner.Remove(source);
                taken = source;
            }
            else
            {
                // SplitOff 只动这一堆，不触碰 owner 的其它条目。
                // ⚠️ 这里返回的是**新建的 Thing**：调用方必须把它写回作业队列目标。
                taken = source.SplitOff(want);
            }
            if (taken == null) return null;

            if (held.TryAdd(taken, false)) return taken;

            Return(owner, taken);
            return null;
        }

        // 队列目标不能合堆销毁, 每件原料都需要独立空位
        private bool CanFit(Thing t)
        {
            ThingOwner held = GetDirectlyHeldThings();
            if (held.Contains(t)) return true;
            return held.Count < Props.capacityStacks;
        }

        /// <summary>退回：原容器 → 脚下 → 背包兜底。三条都失败才报错（正常永不发生）。</summary>
        private void Return(ThingOwner owner, Thing taken)
        {
            if (taken == null || taken.Destroyed) return;
            if (owner != null && owner.TryAdd(taken, true)) return;

            Pawn pawn = Pawn;
            Map map = (pawn == null) ? null : pawn.MapHeld;
            if (map != null && pawn.PositionHeld.IsValid
                && GenPlace.TryPlaceThing(taken, pawn.PositionHeld, map, ThingPlaceMode.Near))
            {
                return;
            }

            if (!GetDirectlyHeldThings().TryAdd(taken, false))
                Log.Error("[DigitalStorage] 背包：物品退不回原容器也放不下：" + taken);
        }

        /// <summary>
        /// 把背包内容退回**最近的可用核心**，返回退回的数量。
        /// 没有可用核心（没造 / 断电 / 不在图上）时原样留在背包里，等下一个检查周期 —— 不丢物。
        /// </summary>
        public int ReturnContentsToCore()
        {
            ThingOwner held = GetDirectlyHeldThings();
            if (held.Count == 0) return 0;

            Pawn pawn = Pawn;
            Map map = (pawn == null) ? null : pawn.MapHeld;
            if (map == null) return 0; // 远行队 / 载具里：跟着小人走，落地时再处理

            Building_StorageCore core = FindNearestCore(map, pawn.PositionHeld);
            // 【跨图】本图一个可用核心都没有 → 退回**任意图**上最近的核心。
            // 场景：小人带着从 a 图取来的料坐运输舱到了 b 图（b 图还没造核心）。
            // 没有这条兜底，那批料会一直躺在背包里（不丢，但等于被藏起来了）。
            // 注意远行队 / 太空里 pawn.MapHeld 为 null ⇒ 上面已经 return 0，料跟着人走。
            if (core == null) core = FindNearestCoreGlobal(pawn.PositionHeld);
            if (core == null) return 0;

            int moved = 0;
            for (int i = held.Count - 1; i >= 0; i--)
            {
                Thing t = held[i];
                if (t == null) continue;

                moved += core.TryStore(t, t.stackCount);
            }
            return moved;
        }

        private static Building_StorageCore FindNearestCore(Map map, IntVec3 from)
        {
            return Nearest(CoreFinder.AllUsableCores(map), from);
        }

        /// <summary>本图没有可用核心时的兜底：全游戏最近的核心（见调用处的跨图注释）。</summary>
        private static Building_StorageCore FindNearestCoreGlobal(IntVec3 from)
        {
            return Nearest(CoreFinder.AllUsableCoresGlobal(), from);
        }

        private static Building_StorageCore Nearest(List<Building_StorageCore> cores, IntVec3 from)
        {
            Building_StorageCore best = null;
            int bestDist = int.MaxValue;

            for (int i = 0; i < cores.Count; i++)
            {
                Building_StorageCore core = cores[i];
                if (core == null) continue;

                int dist = (core.Position - from).LengthHorizontalSquared;
                if (best == null || dist < bestDist)
                {
                    best = core;
                    bestDist = dist;
                }
            }
            return best;
        }

        /// <summary>
        /// 清空到背包之外（落地优先，落不下塞原版背包）。**只在倒地/死亡/hediff 移除时用**，
        /// 正常路径是 <see cref="ReturnContentsToCore"/>。
        /// </summary>
        public void EjectAll()
        {
            ThingOwner held = GetDirectlyHeldThings();
            if (held.Count == 0) return;

            Pawn pawn = Pawn;
            Map map = (pawn == null) ? null : pawn.MapHeld;
            if (map != null && pawn.PositionHeld.IsValid)
                held.TryDropAll(pawn.PositionHeld, map, ThingPlaceMode.Near);

            // 还剩下的 = 地上放不下，或人不在图上（远行队）：交给原版背包，随小人走
            if (held.Count == 0 || pawn == null || pawn.inventory == null || pawn.inventory.innerContainer == null)
                return;

            for (int i = held.Count - 1; i >= 0; i--)
            {
                Thing t = held[i];
                if (t == null) continue;

                Thing taken = held.Take(t, t.stackCount);
                if (taken == null) continue;

                if (!pawn.inventory.innerContainer.TryAdd(taken, true)) Return(held, taken);
            }
        }

        // ===================================================================
        // 默认清空（用户决策 2）
        // ===================================================================

        /// <summary>
        /// 「背包默认是空的」。~250 tick 一次（<c>Pawn.HealthTickInterval</c> → <c>Hediff.TickInterval</c>），
        /// 成本 = 一次字段判空。注意 <c>HealthTickInterval</c> 对**已死亡**的 pawn 直接 return，
        /// 所以死亡那条路走 <c>Patch_BackpackEjectOnDeath</c>。
        /// </summary>
        public override void CompPostTickInterval(ref float severityAdjustment, int delta)
        {
            base.CompPostTickInterval(ref severityAdjustment, delta);
            if (backpack == null || backpack.Count == 0) return;

            // 这条路每 ~250 tick 会跑在**每一个**植入了背包的 pawn 身上（含别的 mod 的机械族），
            // 所以整段自吞异常：我们只是顺手带个背包，绝不该有能力打断别人的 Tick。
            try
            {
                Pawn pawn = Pawn;
                if (pawn == null) return;

                // 倒地 / 死亡：先退核心（干净），退不掉才落地
                if (pawn.Dead || pawn.Downed)
                {
                    ReturnContentsToCore();
                    if (Count > 0) EjectAll();
                    return;
                }

                // 正在做 bill：料还在用（取料 toil 之后、原版把料放到工作台之前的那几 tick）
                Job cur = (pawn.jobs == null) ? null : pawn.jobs.curJob;
                if (cur != null && cur.def == JobDefOf.DoBill) return;

                ReturnContentsToCore();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 背包清理失败（物品仍在背包里，不会丢）：" + e, 0x44534253);
            }
        }

        // ===================================================================
        // 存档 / 生成
        // ===================================================================

        public override void CompExposeData()
        {
            base.CompExposeData();
            Scribe_Deep.Look(ref backpack, "dsBackpack", this);
        }

        public override void CompPostMake()
        {
            base.CompPostMake();
            if (backpack == null) backpack = new ThingOwner<Thing>(this);
        }

        public override void CompPostPostAdd(DamageInfo? dinfo)
        {
            base.CompPostPostAdd(dinfo);
            if (backpack == null) backpack = new ThingOwner<Thing>(this);
        }

        public override void CompPostPostRemoved()
        {
            base.CompPostPostRemoved();
            // hediff 被移除（含死亡后清理）⇒ owner 即将消失，内容物必须先交出去。
            // 同样自吞异常：移除 hediff 的时机由别人决定（读档、派系变更、SpawnSetup），
            // 我们抛出去会把调用方打断在半路。
            try
            {
                EjectAll();
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 背包移除时清空失败（物品仍在背包对象里）：" + e, 0x44534254);
            }
        }
    }
}
