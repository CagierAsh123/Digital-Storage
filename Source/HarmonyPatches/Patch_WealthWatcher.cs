using DigitalStorage.Components;
using DigitalStorage.Services;
using DigitalStorage.Settings;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 将虚拟存储中的物品计入地图财富（可开关）
    /// </summary>
    [HarmonyPatch(typeof(WealthWatcher), "ForceRecount")]
    public static class Patch_WealthWatcher_ForceRecount
    {
        public static void Postfix(Map ___map, ref float ___wealthItems)
        {
            if (!DigitalStorageSettings.countVirtualWealth)
            {
                return;
            }

            if (___map == null)
            {
                return;
            }

            Game game = Current.Game;
            DigitalStorageGameComponent gameComp = game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            float virtualWealth = 0f;
            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered || core.Map != ___map)
                {
                    continue;
                }

                foreach (var item in core.GetAllStoredItems())
                {
                    if (item == null || item.def == null || item.stackCount <= 0)
                    {
                        continue;
                    }

                    Thing thing = item.CreateThing();
                    if (thing == null)
                    {
                        continue;
                    }

                    virtualWealth += thing.MarketValue * item.stackCount;
                }
            }

            if (virtualWealth > 0f)
            {
                ___wealthItems += virtualWealth;
            }
        }
    }
}
