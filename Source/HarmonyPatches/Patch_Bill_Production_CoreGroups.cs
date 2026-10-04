using DigitalStorage.Compatibility;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [HarmonyPatch(typeof(Bill_Production), "SaveSlotReferencable")]
    internal static class Patch_Bill_Production_SaveCoreGroup
    {
        [HarmonyPrefix]
        private static bool Prefix(ISlotGroup slot, string key)
        {
            CoreSlotGroupAdapter adapter = (slot as SlotGroup)?.parent as CoreSlotGroupAdapter;
            if (adapter == null) return true;

            // 替身不参与深度保存, 因此引用已注册的核心身份
            ILoadReferenceable reference = adapter.Core;
            Scribe_References.Look(ref reference, key);
            return false;
        }
    }

    [HarmonyPatch(typeof(Bill_Production), "LoadSlotReferencable")]
    internal static class Patch_Bill_Production_LoadCoreGroup
    {
        [HarmonyPrefix]
        private static bool Prefix(ref ISlotGroup slot, string key)
        {
            ILoadReferenceable reference = null;
            Scribe_References.Look(ref reference, key);
            if (Scribe.mode != LoadSaveMode.ResolvingCrossRefs) return false;

            // 交叉引用解析后才有核心实例, 此时复用它的唯一组
            Building_StorageCore core = reference as Building_StorageCore;
            if (core != null)
            {
                slot = core.CompatSlotGroup.GetSlotGroup();
                return false;
            }

            ISlotGroup group = reference as ISlotGroup;
            if (group != null)
            {
                slot = group;
                return false;
            }

            ISlotGroupParent parent = reference as ISlotGroupParent;
            if (parent != null) slot = parent.GetSlotGroup();
            return false;
        }
    }

    [HarmonyPatch(typeof(Bill_Production), "ValidateGroup")]
    internal static class Patch_Bill_Production_ValidateCoreGroup
    {
        [HarmonyPrefix]
        private static bool Prefix(Bill_Production __instance, ref ISlotGroup slot)
        {
            SlotGroup group = slot as SlotGroup;
            CoreSlotGroupAdapter adapter = group?.parent as CoreSlotGroupAdapter;
            if (adapter == null) return true;

            Map map = __instance.Map;
            if (map != null && !map.haulDestinationManager.AllGroupsListForReading.Contains(group))
            {
                if (__instance != BillUtility.Clipboard)
                {
                    Messages.Message("MessageBillValidationIncludeBuildingDeleted".Translate(
                        __instance.LabelCap,
                        __instance.billStack.billGiver.LabelShort.CapitalizeFirst(),
                        adapter.Core.LabelCap),
                        __instance.billStack.billGiver as Thing, MessageTypeDefOf.NegativeEvent, true);
                }
                slot = null;
            }
            return false;
        }
    }
}
