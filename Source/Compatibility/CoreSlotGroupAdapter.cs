using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using Verse;

namespace DigitalStorage.Compatibility
{
    // 核心需保留原版容器搬运, 所以由零格子替身参与组选择和计数
    internal class CoreSlotGroupAdapter : ISlotGroupParent
    {
        private static readonly List<IntVec3> NoCells = new List<IntVec3>();

        private readonly Building_StorageCore core;
        private SlotGroup slotGroup;

        public CoreSlotGroupAdapter(Building_StorageCore core)
        {
            this.core = core;
        }

        public Building_StorageCore Core => core;

        // ===== IHaulDestination：故意全惰性（见类注释）=====

        public bool HaulDestinationEnabled => false;

        public bool Accepts(Thing t) => false;

        public IntVec3 Position => core.PositionHeld;

        public Map Map => core.Map;

        // ===== IStoreSettingsParent：借核心的（只被排序/优先级显示读）=====

        public StorageSettings GetStoreSettings() => core.GetStoreSettings();

        public StorageSettings GetParentStoreSettings() => core.GetParentStoreSettings();

        public void Notify_SettingsChanged() { }

        public bool StorageTabVisible => false;

        // ===== ISlotGroupParent =====

        public bool IgnoreStoredThingsBeauty => true;

        public string GroupingLabel => core.Label;

        public int GroupingOrder => 0;

        public IEnumerable<IntVec3> AllSlotCells() => NoCells;

        public List<IntVec3> AllSlotCellsList() => NoCells; // 零格子：不占任何格子，格子腿永不选中

        public void Notify_ReceivedThing(Thing newItem) { }

        public void Notify_LostThing(Thing newItem) { }

        public string SlotYielderLabel() => core.Label;

        public SlotGroup GetSlotGroup()
        {
            if (slotGroup == null) slotGroup = new SlotGroup(this);
            return slotGroup;
        }
    }
}
