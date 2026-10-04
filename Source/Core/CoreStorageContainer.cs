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
            return base.GetCountCanAccept(item, canMergeWithExistingStacks);
        }

        public override bool TryAdd(Thing item, bool canMergeWithExistingStacks = true)
        {
            // 整堆接口必须全部可收, 避免失败前已部分合堆
            if (item == null || GetCountCanAccept(item, canMergeWithExistingStacks) < item.stackCount) return false;
            return base.TryAdd(item, canMergeWithExistingStacks);
        }

        public override int TryAdd(Thing item, int count, bool canMergeWithExistingStacks = true)
        {
            if (item == null || count <= 0) return 0;
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
