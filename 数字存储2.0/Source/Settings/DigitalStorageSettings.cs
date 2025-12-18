using UnityEngine;
using Verse;

namespace DigitalStorage.Settings
{
    public class DigitalStorageSettings : ModSettings
    {
        public static float costMultiplier = 1.0f;
        public static bool enableDebugLog = false;
        public static int reservedCountPerItem = 100; // 每种物品预留数量

        public override void ExposeData()
        {
            Scribe_Values.Look(ref costMultiplier, "costMultiplier", 1.0f);
            Scribe_Values.Look(ref enableDebugLog, "enableDebugLog", false);
            Scribe_Values.Look(ref reservedCountPerItem, "reservedCountPerItem", 100);
            base.ExposeData();
        }

        public static void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listingStandard = new Listing_Standard();
            listingStandard.Begin(inRect);

            // ========== 造价设置 ==========
            Text.Font = GameFont.Medium;
            listingStandard.Label("造价设置");
            Text.Font = GameFont.Small;
            listingStandard.Gap(12f);

            listingStandard.Label(string.Format("造价倍率: {0:F1}x", costMultiplier));
            costMultiplier = listingStandard.Slider(costMultiplier, 0.1f, 20f);
            listingStandard.Gap(6f);

            listingStandard.Label("调整所有建筑和物品的材料消耗");
            listingStandard.Gap(24f);

            // ========== 预留物品设置 ==========
            Text.Font = GameFont.Medium;
            listingStandard.Label("预留物品设置");
            Text.Font = GameFont.Small;
            listingStandard.Gap(12f);

            listingStandard.Label(string.Format("每种物品预留数量: {0}", reservedCountPerItem));
            reservedCountPerItem = (int)listingStandard.Slider(reservedCountPerItem, 10, 500);
            listingStandard.Gap(6f);

            listingStandard.Label("预留物品保持真实状态，WorkGiver 可以直接找到");
            listingStandard.Gap(24f);

            // ========== 调试设置 ==========
            Text.Font = GameFont.Medium;
            listingStandard.Label("调试设置");
            Text.Font = GameFont.Small;
            listingStandard.Gap(12f);

            listingStandard.CheckboxLabeled("启用详细日志", ref enableDebugLog, 
                "在日志中显示详细的调试信息");

            listingStandard.End();
        }
    }
}

