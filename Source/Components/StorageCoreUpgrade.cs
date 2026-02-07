using System.Collections.Generic;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 升级材料消耗定义
    /// </summary>
    public class UpgradeCostEntry
    {
        public ThingDef thingDef;
        public int count;
    }

    /// <summary>
    /// 存储核心升级等级数据
    /// </summary>
    public class StorageCoreUpgrade
    {
        /// <summary>等级名称（翻译键）</summary>
        public string labelKey;

        /// <summary>该等级的存储容量</summary>
        public int capacity = 100;

        /// <summary>该等级的耗电量</summary>
        public float powerConsumption = 100f;

        /// <summary>升级到此等级所需的 tick 数</summary>
        public int upgradeDurationTicks = 30000;

        /// <summary>升级完成后的冷却 tick 数</summary>
        public int cooldownTicks = 2500;

        /// <summary>升级到此等级所需的材料</summary>
        public List<UpgradeCostEntry> upgradeCost;

        public string GetLabel()
        {
            if (!string.IsNullOrEmpty(labelKey))
            {
                return labelKey.Translate();
            }
            return "Level " + capacity;
        }

        public string GetUpgradeCostString()
        {
            if (upgradeCost == null || upgradeCost.Count == 0)
            {
                return "";
            }

            List<string> parts = new List<string>();
            foreach (UpgradeCostEntry cost in upgradeCost)
            {
                if (cost == null || cost.thingDef == null) continue;
                parts.Add(cost.thingDef.label + " x" + cost.count);
            }
            return string.Join(", ", parts);
        }
    }
}
