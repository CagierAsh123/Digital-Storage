using System;
using RimWorld;
using Verse;

namespace DigitalStorage.Data
{
    /// <summary>
    /// 虚拟存储的物品数据，不保存真实 Thing 对象
    /// </summary>
    public class StoredItemData : IExposable
    {
        public ThingDef def;
        public ThingDef stuffDef;
        public QualityCategory quality = QualityCategory.Normal;
        public int hitPoints = -1;
        public int stackCount = 1;
        
        // 用于唯一标识，方便查找和修改
        public string uniqueId;

        public StoredItemData()
        {
            this.uniqueId = Guid.NewGuid().ToString();
        }

        public StoredItemData(Thing thing)
        {
            this.def = thing.def;
            this.stuffDef = thing.Stuff;
            this.stackCount = thing.stackCount;
            this.hitPoints = thing.HitPoints;
            this.uniqueId = Guid.NewGuid().ToString();

            // 提取品质
            CompQuality compQuality = thing.TryGetComp<CompQuality>();
            if (compQuality != null)
            {
                this.quality = compQuality.Quality;
            }
        }

        public void ExposeData()
        {
            Scribe_Defs.Look(ref this.def, "def");
            Scribe_Defs.Look(ref this.stuffDef, "stuffDef");
            Scribe_Values.Look(ref this.quality, "quality", QualityCategory.Normal);
            Scribe_Values.Look(ref this.hitPoints, "hitPoints", -1);
            Scribe_Values.Look(ref this.stackCount, "stackCount", 1);
            Scribe_Values.Look(ref this.uniqueId, "uniqueId", null);

            if (Scribe.mode == LoadSaveMode.PostLoadInit && string.IsNullOrEmpty(this.uniqueId))
            {
                this.uniqueId = Guid.NewGuid().ToString();
            }
        }

        /// <summary>
        /// 根据数据生成真实 Thing
        /// </summary>
        public Thing CreateThing()
        {
            if (this.def == null)
            {
                return null;
            }

            Thing thing = ThingMaker.MakeThing(this.def, this.stuffDef);
            thing.stackCount = this.stackCount;

            if (this.hitPoints > 0)
            {
                thing.HitPoints = this.hitPoints;
            }

            CompQuality compQuality = thing.TryGetComp<CompQuality>();
            if (compQuality != null)
            {
                compQuality.SetQuality(this.quality, ArtGenerationContext.Colony);
            }

            return thing;
        }

        /// <summary>
        /// 检查是否匹配指定的 ThingDef 和 Stuff
        /// </summary>
        public bool Matches(ThingDef thingDef, ThingDef stuff = null)
        {
            return this.def == thingDef && this.stuffDef == stuff;
        }

        public override string ToString()
        {
            string label = this.def?.label ?? "Unknown";
            if (this.stuffDef != null)
            {
                label = this.stuffDef.label + " " + label;
            }
            return $"{label} x{this.stackCount}";
        }
    }
}

