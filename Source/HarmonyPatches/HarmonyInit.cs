using HarmonyLib;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [StaticConstructorOnStartup]
    public static class HarmonyInit
    {
        static HarmonyInit()
        {
            Harmony harmony = new Harmony("DigitalStorage.HarmonyPatches");
            harmony.PatchAll();

            // 软依赖兼容补丁（Phinix / PhinixRedPacket）
            PhinixCompatibility.TryApplyPatches(harmony);

            Log.Message("[DigitalStorage 2.0] Harmony patches applied.");
        }
    }
}

