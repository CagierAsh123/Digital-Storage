using DigitalStorage.Core;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// <b>按药物政策备货 / 按库存策略备货</b>：小人从核心把药（或任意库存策略物品）取进背包。
    ///
    /// <para><b>两个入口、同一种缺口</b>：
    /// <c>JobGiver_MoveDrugsToInventory.cs:52</c> <c>FindDrugFor</c> 与
    /// <c>JobGiver_TakeForInventoryStock.cs:46</c> <c>FindThingFor</c>，
    /// 都是 <c>GenClosest.ClosestThingReachable(...)</c> 不传 <c>lookInHaulSources</c>
    /// ⇒ 只看已生成的东西（<c>Verse/GenClosest.cs:40</c> 默认 false）。</para>
    ///
    /// <para><b>必须取出</b>：下游 <c>JobDriver_TakeInventory.cs:20</c> 与
    /// <c>JobDriver_TakeCountToInventory.cs:16</c> 的 GotoThing 都没有
    /// <c>canGotoSpawnedParent</c>，且带 <c>FailOnDespawnedNullOrForbidden</c>
    /// ⇒ 容器内容物进了 job 会首 tick 静默失败。</para>
    ///
    /// <para><b>只取"刚好够"的数量</b>：两个 giver 都会用
    /// <c>min(需要数, thing.stackCount)</c> 设 <c>job.count</c>。若我们整堆取出，多出来的部分
    /// 会留在小人脚下（再被自动收纳吸回，形成取-吸循环）。所以这里按原版同一个算式先算出
    /// 需要多少，再只取那么多。</para>
    /// </summary>
    static class Patch_TakeToInventoryFromCore
    {
        /// <summary>核心（及本图上其它容器）里找一件符合谓词的东西；本图优先、再看其它图核心。</summary>
        internal static Thing FindInStorage(Pawn pawn, System.Predicate<Thing> validator)
        {
            if (pawn?.MapHeld == null || validator == null) return null;
            return HaulSourceContents.FindBestIncludingRemote(pawn.MapHeld, null, validator);
        }

        /// <summary>取出 <paramref name="count"/> 件放到小人脚下；失败返回 null（东西留在原处）。</summary>
        internal static Thing TakeToFeet(Pawn pawn, Thing found, int count)
        {
            if (found == null || count <= 0) return null;
            return HaulSourceContents.ExtractToFeet(found, Mathf.Min(count, found.stackCount), pawn);
        }
    }

    /// <summary>① 按药物政策备药（<c>JobGiver_MoveDrugsToInventory</c>）。</summary>
    [HarmonyPatch(typeof(JobGiver_MoveDrugsToInventory), "FindDrugFor")]
    static class Patch_MoveDrugsToInventoryFromCore
    {
        static void Postfix(Pawn pawn, ThingDef drugDef, int desired, ref Thing __result)
        {
            if (__result != null) return;
            if (drugDef == null || desired <= 0) return;   // :37 已按库存算好还差几件；<=0 表示不差
            if (!ConsumePatchUtil.PawnMayUseCore(pawn)) return;
            if (!drugDef.IsDrug) return;                   // :57

            // 与 :55-:72 的 validator 同口径：不禁用 + 可预约（数量按 min(堆量, 需要数) 预约）。
            Thing found = Patch_TakeToInventoryFromCore.FindInStorage(
                pawn,
                x => x.def == drugDef
                     && !x.IsForbidden(pawn)
                     && pawn.CanReserve(x, 10, Mathf.Min(x.stackCount, desired)));
            if (found == null) return;

            Thing taken = Patch_TakeToInventoryFromCore.TakeToFeet(pawn, found, desired);
            if (taken != null) __result = taken;
        }
    }

    /// <summary>② 按库存策略备货（<c>JobGiver_TakeForInventoryStock</c>）。</summary>
    [HarmonyPatch(typeof(JobGiver_TakeForInventoryStock), "FindThingFor")]
    static class Patch_InventoryStockFromCore
    {
        static void Postfix(Pawn pawn, ThingDef thingDef, ref Thing __result)
        {
            if (__result != null || thingDef == null) return;
            if (!ConsumePatchUtil.PawnMayUseCore(pawn)) return;

            int desired = DesiredFor(pawn, thingDef);
            if (desired <= 0) return;

            // 与 :49-:60 的 validator 同口径。
            Thing found = Patch_TakeToInventoryFromCore.FindInStorage(
                pawn,
                x => x.def == thingDef
                     && !x.IsForbidden(pawn)
                     && pawn.CanReserve(x, 10));
            if (found == null) return;

            Thing taken = Patch_TakeToInventoryFromCore.TakeToFeet(pawn, found, desired);
            if (taken != null) __result = taken;
        }

        /// <summary>
        /// 原版 <c>TryGiveJob</c> 里的算式（<c>:29-:35</c>）：
        /// <c>entry.count - pawn.inventory.Count(thingDef)</c>。私有方法拿不到那个 entry，只能自己找。
        /// </summary>
        private static int DesiredFor(Pawn pawn, ThingDef thingDef)
        {
            Pawn_InventoryStockTracker stock = pawn.inventoryStock;
            if (stock?.stockEntries == null) return 0;

            foreach (InventoryStockEntry entry in stock.stockEntries.Values)
            {
                if (entry?.thingDef == thingDef) return entry.count - pawn.inventory.Count(thingDef);
            }
            return 0;
        }
    }
}
