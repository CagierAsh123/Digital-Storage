using System;
using DigitalStorage.Components;
using Verse;

namespace DigitalStorage.Core
{
    public sealed class CoreStorageContainer : ThingOwner<Thing>
    {
        public CoreStorageContainer(IThingHolder owner) : base(owner)
        {
            dontTickContents = true;
        }

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

        public override bool TryAdd(Thing item, bool canMergeWithExistingStacks = true)
        {
            // 整堆接口必须全部可收, 避免失败前已部分合堆
            if (item == null || GetCountCanAccept(item, canMergeWithExistingStacks) < item.stackCount) return false;
            if (!OwnerAllowsInsert(item)) return false;
            return base.TryAdd(item, canMergeWithExistingStacks);
        }

        public override int TryAdd(Thing item, int count, bool canMergeWithExistingStacks = true)
        {
            if (item == null || count <= 0) return 0;
            if (!OwnerAllowsInsert(item)) return 0;
            int accepted = Math.Min(count, GetCountCanAccept(item, canMergeWithExistingStacks));
            return base.TryAdd(item, accepted, canMergeWithExistingStacks);
        }

        internal void AddLoaded(Thing item)
        {
            item.holdingOwner = this;
            InnerListForReading.Add(item);
        }
    }
}
