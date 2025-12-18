using RimWorld;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 虚拟材料标记组件的属性
    /// </summary>
    public class CompProperties_VirtualIngredient : CompProperties
    {
        public CompProperties_VirtualIngredient()
        {
            this.compClass = typeof(CompVirtualIngredient);
        }
    }

    /// <summary>
    /// 虚拟材料标记组件
    /// 用于标记临时创建的虚拟 Thing，在 StartJob 时替换为真实物品
    /// </summary>
    public class CompVirtualIngredient : ThingComp
    {
        private bool isVirtual = false;
        private Building_StorageCore sourceCore;
        private Map sourceMap;
        
        // 预留物品相关
        private bool isFromReserved = false;  // 是否来自预留物品
        private Thing reservedThing = null;   // 预留物品的引用（如果来自预留物品）

        public bool IsVirtual => isVirtual;

        public Building_StorageCore SourceCore => sourceCore;

        public Map SourceMap => sourceMap;

        public bool IsFromReserved => isFromReserved;

        public Thing ReservedThing => reservedThing;

        public void SetVirtual(bool value)
        {
            isVirtual = value;
        }

        public void SetSourceCore(Building_StorageCore core)
        {
            sourceCore = core;
        }

        public void SetSourceMap(Map map)
        {
            sourceMap = map;
        }

        public void SetFromReserved(bool value, Thing reservedThing = null)
        {
            isFromReserved = value;
            this.reservedThing = reservedThing;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref isVirtual, "isVirtual", false);
            Scribe_References.Look(ref sourceCore, "sourceCore", false);
            Scribe_Values.Look(ref isFromReserved, "isFromReserved", false);
            Scribe_References.Look(ref reservedThing, "reservedThing", false);
            // Map 不能直接序列化，需要在加载时重建
        }
    }
}

