using UnityEngine;
using Verse;

namespace DigitalStorage.Settings
{
    public class DigitalStorageSettings : ModSettings
    {
        public static float costMultiplier = 1.0f;
        public static bool enableDebugLog = false;
        public static bool enableConversionLog = false; // 转换日志（Tick检查、AsyncItemConverter等）
        public static int reservedCountPerItem = 100; // 每种物品预留数量
        public static bool countVirtualWealth = true; // 虚拟物品是否计入财富
        public static bool interfaceInstantDigitize = true; // 输入接口是否直接数字化
        public static int EffectiveReservedCount => Mathf.Clamp(reservedCountPerItem, 10, 1000);

        public override void ExposeData()
        {
            Scribe_Values.Look(ref costMultiplier, "costMultiplier", 1.0f);
            Scribe_Values.Look(ref enableDebugLog, "enableDebugLog", false);
            Scribe_Values.Look(ref enableConversionLog, "enableConversionLog", false);
            Scribe_Values.Look(ref reservedCountPerItem, "reservedCountPerItem", 100);
            Scribe_Values.Look(ref countVirtualWealth, "countVirtualWealth", true);
            Scribe_Values.Look(ref interfaceInstantDigitize, "interfaceInstantDigitize", true);
            reservedCountPerItem = Mathf.Clamp(reservedCountPerItem, 10, 1000);
            base.ExposeData();
        }

        public static void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listingStandard = new Listing_Standard();
            listingStandard.Begin(inRect);

            // ========== 造价设置 ==========
            Text.Font = GameFont.Medium;
            listingStandard.Label("DS_CostSettings".Translate());
            Text.Font = GameFont.Small;
            listingStandard.Gap(12f);

            listingStandard.Label("DS_CostMultiplier".Translate(costMultiplier));
            costMultiplier = listingStandard.Slider(costMultiplier, 0.1f, 20f);
            listingStandard.Gap(6f);

            listingStandard.Label("DS_CostMultiplierDesc".Translate());
            listingStandard.Gap(24f);

            // ========== 预留物品设置 ==========
            Text.Font = GameFont.Medium;
            listingStandard.Label("DS_ReservedSettings".Translate());
            Text.Font = GameFont.Small;
            listingStandard.Gap(12f);

            listingStandard.Label("DS_ReservedCountPerItem".Translate(reservedCountPerItem));
            reservedCountPerItem = (int)listingStandard.Slider(reservedCountPerItem, 10, 1000);
            listingStandard.Gap(6f);

            listingStandard.Label("DS_ReservedDesc".Translate());
            listingStandard.Gap(24f);

            // ========== 调试设置 ==========
            Text.Font = GameFont.Medium;
            listingStandard.Label("DS_DebugSettings".Translate());
            Text.Font = GameFont.Small;
            listingStandard.Gap(12f);

            listingStandard.CheckboxLabeled("DS_EnableDebugLog".Translate(), ref enableDebugLog, 
                "DS_EnableDebugLogDesc".Translate());
            listingStandard.Gap(6f);
            
            listingStandard.CheckboxLabeled("DS_EnableConversionLog".Translate(), ref enableConversionLog, 
                "DS_EnableConversionLogDesc".Translate());
            listingStandard.Gap(6f);

            listingStandard.CheckboxLabeled("DS_CountVirtualWealth".Translate(), ref countVirtualWealth,
                "DS_CountVirtualWealthDesc".Translate());
            listingStandard.Gap(6f);

            listingStandard.CheckboxLabeled("DS_InterfaceInstantDigitize".Translate(), ref interfaceInstantDigitize,
                "DS_InterfaceInstantDigitizeDesc".Translate());
            listingStandard.Gap(6f);
            
            // 警告
            GUI.color = Color.yellow;
            listingStandard.Label("DS_DebugWarning".Translate());
            GUI.color = Color.white;

            listingStandard.End();
        }
    }
}

