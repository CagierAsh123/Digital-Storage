using System;
using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using RimWorld;
using Verse;

namespace DigitalStorage.Core
{
    public sealed class CoreStorageContainer : ThingOwner<Thing>
    {
        public CoreStorageContainer(IThingHolder owner) : base(owner)
        {
            dontTickContents = true;
        }

        /// 合并碎堆时的快照缓冲（复用，避免每轮 new List）
        private readonly List<Thing> consolidateBuf = new List<Thing>();

        private void UpdateCapacity()
        {
            // 旧档超额库存保留, 但不能再新增堆
            maxStacks = Math.Max(Count, ((Building_StorageCore)Owner).maxStacks);
        }

        public override int GetCountCanAccept(Thing item, bool canMergeWithExistingStacks = true)
        {
            UpdateCapacity();
            return CountCanAcceptSafe(item, canMergeWithExistingStacks);
        }

        /// <summary>
        /// 容量判定的 <b>long</b> 版。
        ///
        /// <para>为什么不能直接用原版 <c>ThingOwner.GetCountCanAccept</c>：它算的是
        /// <c>int num += (maxStacks - Count) * item.def.stackLimit</c> —— 纯 int 乘法，没有溢出保护。
        /// 打了堆叠类 mod 的整合包里 <c>stackLimit</c> 会被放大到百万级：实测玩家环境
        /// 白银 = 30000（simple.tech 的 XML）× 800（OgreStack SmallVolumeResource Scalar）=
        /// <b>24,000,000</b>；零部件 = 750 × 5000 = <b>3,750,000</b>。
        /// 核心 13/3000 时还剩 2987 个堆位，<c>2987 × 24,000,000 = 71,688,000,000</c>
        /// 截断成 int 是 <b>-1,326,444,032</b> ⇒ <c>GetCountCanAccept</c> 返回负数 ⇒
        /// <c>Accepts</c> 判"装不下" ⇒ 原版容器腿跳过核心，我们的自动收纳
        /// （<c>WouldVanillaHaulIntoCore</c> 先问原版）也把这类东西全丢掉。
        /// 症状就是"地面白银/零部件死活不进核心"，而且一点红字都没有。</para>
        ///
        /// <para>语义与原版逐句对齐，只把累加器换成 <see cref="long"/>，结果夹到 [0, stackCount]。</para>
        /// </summary>
        internal int CountCanAcceptSafe(Thing item, bool canMergeWithExistingStacks = true)
        {
            // 自刷新（幂等 O(1)）：诊断工具直接调本方法时不经过 GetCountCanAccept，
            // 若容器上的 maxStacks 还是哨兵 999999 会误报"无限容量"。
            UpdateCapacity();
            if (item == null || item.def == null || item.stackCount <= 0) return 0;
            if (maxStacks == 999999) return item.stackCount;

            long num = 0L;
            if (Count < maxStacks)
            {
                num += (long)(maxStacks - Count) * item.def.stackLimit;
            }
            if (num >= item.stackCount) return item.stackCount;

            if (canMergeWithExistingStacks)
            {
                for (int i = 0, count = Count; i < count; i++)
                {
                    Thing at = GetAt(i);
                    if (at == null || at.def == null) continue;
                    if (at.stackCount < at.def.stackLimit && at.CanStackWith(item))
                    {
                        num += (long)(at.def.stackLimit - at.stackCount);
                        if (num >= item.stackCount) return item.stackCount;
                    }
                }
            }

            if (num <= 0L) return 0;
            return (int)Math.Min(num, item.stackCount);
        }

        /// <summary>
        /// 插入漏斗上的**玩家规则**（筛选 / 入库开关 / 电力）。
        ///
        /// <para>容量之外还要管这三样，是因为 <c>IHaulDestination.Accepts</c> 只在**选目的地**时被问
        /// （<c>StoreUtility.TryFindBestBetterStorageFor</c>）；真正的插入走 <c>ThingOwner.TryAdd</c>：
        /// 原版 <c>Toils_Haul.DepositHauledThingInContainer</c> 如此，
        /// "先组批、再一件件塞"的搬运 mod（Pick Up And Haul 的 <c>JobDriver_UnloadYourHauledInventory</c>）
        /// 更是如此 —— 它组批时只问 <c>CanAcceptAnyOf</c>（= <c>GetCountCanAccept &gt; 0</c>，只看容量）。
        /// 不过滤的话，玩家明确不收的东西会从这条路进核心。</para>
        ///
        /// <para><c>Spawned == false</c> 一律放行：读档 / 迁移期电力与开关都还没定论，
        /// 那时走 <see cref="AddLoaded"/> 与 <c>Building_StorageCore.AddRestored</c>。</para>
        /// </summary>
        private bool OwnerAllowsInsert(Thing item)
        {
            Building_StorageCore core = Owner as Building_StorageCore;
            if (core == null || !core.Spawned) return true;
            return core.AllowsInsert(item);
        }

        /// <summary>
        /// 注意 <paramref name="canMergeWithExistingStacks"/>：传 <c>false</c> 会**跳过原版的合并循环**，
        /// 只要还有空槽就 append 成新条目 ⇒ 同 def 两堆、<c>Count</c> 虚增、ITab 容量条虚高。
        /// 只有"该 Thing 被外部按引用持有（作业队列 / 预订单 / 装备槽）"时才允许传 false；
        /// 回滚、退回、二次入库一律传 <c>true</c>。
        /// </summary>
        public override bool TryAdd(Thing item, bool canMergeWithExistingStacks = true)
        {
            if (item == null) return false;
            int? moved = HandOverOwned(item, item.stackCount, canMergeWithExistingStacks);
            if (moved.HasValue) return moved.Value == item.stackCount;
            // 整堆接口必须全部可收, 避免失败前已部分合堆
            if (GetCountCanAccept(item, canMergeWithExistingStacks) < item.stackCount) return false;
            if (!OwnerAllowsInsert(item)) return false;
            return base.TryAdd(item, canMergeWithExistingStacks);
        }

        public override int TryAdd(Thing item, int count, bool canMergeWithExistingStacks = true)
        {
            if (item == null || count <= 0) return 0;
            int? moved = HandOverOwned(item, count, canMergeWithExistingStacks);
            if (moved.HasValue) return moved.Value;
            if (!OwnerAllowsInsert(item)) return 0;
            int accepted = Math.Min(count, GetCountCanAccept(item, canMergeWithExistingStacks));
            if (accepted <= 0) return 0;
            return base.TryAdd(item, accepted, canMergeWithExistingStacks);
        }

        /// <summary>
        /// 物品身上还挂着**别的**容器时，替调用方做正规转移（拆堆 → 摘除 → 塞入，失败自回滚）。
        ///
        /// <para><b>为什么必须拦这一手</b>：原版 <c>ThingOwner.TryAdd</c> 遇到这种物品只会打
        /// <c>Tried to add … but this thing is already in another container. owner=…, current container owner=…</c>
        /// 然后返回 false —— 物品留在原地。若那个"原地"其实是个**无主容器**
        /// （<c>Verse.GenLeaving</c> 落地失败时会 <c>break</c>，剩下的东西就住在一个
        /// <c>new ThingOwner&lt;Thing&gt;()</c> 里，Owner 为 null），物品就变成
        /// 既不在图上、也不属于任何容器的游离物：<c>MapHeld == null</c> ⇒ 原版顺手搬运扫描
        /// 里 <c>Fogged()</c> 空引用刷屏。2026-10-22 的报障正是这条链。
        /// 唯一的正确 API 是 <c>TryTransferToContainer</c>，所以这里补上，而不是跟着拒收。</para>
        ///
        /// <para>返回 <c>null</c> 表示"不需要转移"（物品本来就是自由的 / 已经在这个容器里）。</para>
        /// </summary>
        private int? HandOverOwned(Thing item, int count, bool canMergeWithExistingStacks)
        {
            ThingOwner holder = item.holdingOwner;
            if (holder == null || ReferenceEquals(holder, this)) return null;
            // 已落地的物品在 Map 的持有容器里: 必须先 Despawn, 不能当容器间转移
            if (item.Spawned) return 0;
            return holder.TryTransferToContainer(item, this, Math.Min(count, item.stackCount), canMergeWithExistingStacks);
        }


        /// <summary>
        /// 把同种可堆叠的碎堆并成满堆 —— 旧档 / 历史 <c>canMerge=false</c> 路径留下的重复小堆自愈。
        ///
        /// <para><b>只做减法</b>：只减少 <see cref="ThingOwner.Count"/>，绝不会超额，所以顺手能把
        /// 旧档里 <c>Count &gt; cap</c> 的超额库存压回上限内（被吸收方 <c>Destroy()</c> 会自动
        /// <c>Remove</c>，不需要手动摘除 —— 手动摘除已销毁对象反而会 <c>RemoveAt(-1)</c> 抛异常）。</para>
        ///
        /// <para><b>三条铁律</b>（2026-10-23 审计）：① 不能并掉<b>被预约 / 被作业引用</b>的堆 ——
        /// 销毁后 <c>ReservationManager</c> 预订单与 <c>CraftLine.Ingredients</c> 会指向 Destroyed
        /// 目标，表现为"作业卡住"；② 必须 <c>respectStackLimit: true</c>，否则顶破 stackLimit；
        /// ③ 带 HP 的堆只在 HP 相同时合并（原版按数量加权平均会抹平个体差异），带品质的不合并
        /// （合堆会丢品质）。</para>
        ///
        /// <para>返回实际合并对数。<paramref name="maxMerges"/> 用于限制单次开销。</para>
        /// </summary>
        internal int ConsolidateStacks(int maxMerges)
        {
            if (maxMerges <= 0 || Count < 2) return 0;
            Building_StorageCore core = Owner as Building_StorageCore;
            if (core == null || !core.Spawned) return 0;
            Map map = core.Map;
            if (map == null) return 0;

            // 先快照：合并会 Destroy 掉堆（从 innerList 移除），直接按索引遍历会错位
            consolidateBuf.Clear();
            consolidateBuf.AddRange(InnerListForReading);
            int merges = 0;
            for (int i = 0; i < consolidateBuf.Count && merges < maxMerges; i++)
            {
                Thing src = consolidateBuf[i];
                if (src == null || src.Destroyed || src.stackCount <= 0) continue;
                if (src.stackCount >= src.def.stackLimit) continue;
                if (StackIsBusy(src, map)) continue;

                for (int k = 0; k < consolidateBuf.Count && merges < maxMerges; k++)
                {
                    if (k == i) continue;
                    Thing dst = consolidateBuf[k];
                    if (dst == null || dst.Destroyed || ReferenceEquals(dst, src)) continue;
                    if (!dst.CanStackWith(src)) continue;
                    if (dst.stackCount >= dst.def.stackLimit) continue;
                    if (dst.def.useHitPoints && dst.HitPoints != src.HitPoints) continue;
                    if (dst.TryGetComp<CompQuality>() != null) continue;
                    if (StackIsBusy(dst, map)) continue;

                    int take = Math.Min(src.stackCount, dst.def.stackLimit - dst.stackCount);
                    if (take <= 0) continue;
                    Thing piece = src.SplitOff(take);
                    if (piece == null) break;
                    if (!dst.TryAbsorbStack(piece, true))
                    {
                        // 并失败（极少）：塞回去，再换一对
                        TryAdd(piece, true);
                        break;
                    }
                    merges++;
                    if (src.Destroyed || src.stackCount <= 0) break;
                }
            }
            return merges;
        }

        /// <summary>这一堆此刻能不能动：被预订单或制作代理认领的都不能并。</summary>
        private static bool StackIsBusy(Thing t, Map map)
        {
            if (CraftIngredientClaims.OwnerOf(t) != null) return true;
            if (map != null && map.reservationManager.IsReservedByAnyoneOf(t, Faction.OfPlayer)) return true;
            return false;
        }

        internal void AddLoaded(Thing item)
        {
            item.holdingOwner = this;
            InnerListForReading.Add(item);
        }
    }
}
