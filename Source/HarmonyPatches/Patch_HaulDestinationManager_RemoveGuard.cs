using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 把原版「Removing haul destination that isn't registered …」红字降级成开发者模式日志。
    ///
    /// <para><b>触发路径</b>（玩家存档实测的完整栈）：<c>Zone.Delete(bool)</c> →
    /// <c>Zone_Stockpile.RemoveCell</c> → <c>Zone.RemoveCell</c> → <c>Zone.Deregister</c> →
    /// <c>ZoneManager.DeregisterZone</c> → <c>Zone.PostDeregister</c> →
    /// <c>HaulDestinationManager.RemoveHaulDestination</c>。
    /// 这个储存区此刻**已经不在** <c>allHaulDestinationsInOrder</c> 里
    /// （常见良性成因：删格子的循环中最后一格先触发了 deregister，随后 <c>Delete</c> 再走一次；
    /// 或读档后该区从未被 <c>PostRegister</c>/<c>CheckAddHaulDestination</c> 重新登记）。</para>
    ///
    /// <para><b>为什么算良性</b>：原版 <c>RemoveHaulDestination</c> 的这个分支本身就只是
    /// <c>Log.Error</c> + <c>return</c>，不改 <c>allHaulDestinationsInOrder</c>、不改
    /// <c>allGroupsInOrder</c> —— 清单状态与"什么都不做"完全一致。
    /// 我们的前缀只是**提前返回**，行为等价，差别仅是没有那条 error 级日志。</para>
    ///
    /// <para><b>不要**在"在表里"时也拦下</b>：真·重复登记（本 mod 的兼容替身
    /// <c>CoreSlotGroupAdapter</c> 重复 Add）应该继续让原版报出来，所以只在
    /// <c>!Contains</c> 时短路。同理也不加 <c>__result</c>（方法返回 void）。</para>
    /// </summary>
    [HarmonyPatch(typeof(HaulDestinationManager), nameof(HaulDestinationManager.RemoveHaulDestination))]
    internal static class Patch_HaulDestinationManager_RemoveGuard
    {
        private static bool Prefix(HaulDestinationManager __instance, IHaulDestination haulDestination)
        {
            if (__instance == null || haulDestination == null) return true;
            if (__instance.AllHaulDestinationsListForReading.Contains(haulDestination)) return true;

            if (Prefs.DevMode)
            {
                Log.Message("[DigitalStorage] 跳过未登记搬运目的地的重复移除（原版此处报红字）："
                    + haulDestination.ToStringSafe());
            }
            return false;
        }
    }
}
