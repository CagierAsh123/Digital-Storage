// =====================================================================================
//  原版投递到核心：把"整堆全有全无"改成"能收多少收多少"
// -------------------------------------------------------------------------------------
//  这是社区反复报的「小人把东西拿出来又塞回去 / 搬来搬去 / 莫名卡住」的**结构性主因**：
//
//  1. 我们自己的容器 `CoreStorageContainer.TryAdd(Thing,bool)` 是**整堆接口**：
//     `if (GetCountCanAccept(item, canMerge) < item.stackCount) return false;`
//     （注释写明"整堆接口必须全部可收，避免失败前已部分合堆"）。
//  2. 原版搬运投递走 `Verse\ThingOwner.cs:715` `TryTransferToContainer`：
//     `SplitOff(num)` → `otherContainer.TryAdd(thing)` 失败 ⇒ 回吸 ⇒ 返回 0。
//  3. 但投递前的 fail 条件是 `Toils_Haul.cs:403` 的 `CanAcceptAnyOf`
//     （= `GetCountCanAccept > 0`，只看"有没有一点空位"）⇒ **不会提前中止**。
//     `Toils_Haul.cs:442-443` 对 `num2 == 0` 也**不报错不失败**。
//  4. 于是：核心还有零散空位（例：一堆 30/75 + 其它半栈）= 原版认为"能存"，
//     小人把 75 件搬过去 ⇒ 整堆塞不进 ⇒ 就地掉在核心旁边
//     （`Verse.AI\Pawn_JobTracker.cs:510-517`）⇒ 它还在可搬运表里、核心仍是最佳目的地
//     ⇒ 又派同一趟。**无限循环，且完全不依赖任何第三方 mod。**
//
//  修法：在 `TryTransferToContainer` 进核心之前，把"要转多少"夹到
//  `GetCountCanAccept` 那么多 ⇒ 原版 SplitOff 出正好塞得下的那一份、TryAdd 成功、
//  返回非 0（投递算成功、触发 Notify_HauledTo），余料继续留在小人手上由原版正常处理。
//  这与我们自己的 `TryStore(t, count)` 语义一致（能收多少收多少）。
// =====================================================================================

using DigitalStorage.Core;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    internal static class Patch_ThingOwner_TransferToCore_PartialDeposit
    {
        [HarmonyPatch(typeof(ThingOwner), nameof(ThingOwner.TryTransferToContainer),
            new[] { typeof(Thing), typeof(ThingOwner), typeof(int), typeof(bool) })]
        [HarmonyPrefix]
        private static void Prefix(Thing item, ThingOwner otherContainer, ref int count,
            bool canMergeWithExistingStacks)
        {
            if (item == null || item.Destroyed) return;
            if (!(otherContainer is CoreStorageContainer)) return;
            if (item.stackCount <= 0 || count <= 0) return;

            int want = Mathf.Min(count, item.stackCount);
            int fits = otherContainer.GetCountCanAccept(item, canMergeWithExistingStacks);
            if (fits <= 0) return;              // 一个都收不了：交给原版走失败/回吸路径，不在这里丢东西
            if (fits < want) count = fits;      // 只转能装下的那一份，余料留在原持有者（小人手上）
        }
    }
}
