using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 防御 patch（原版 null 缺陷）：PawnCanAutomaticallyHaul 对未 Spawned 物品
    /// （t.Map == null）会在 t.Position.Fogged(t.Map) 处直接 NRE——
    /// GridsUtility.Fogged(IntVec3, Map) 无 null 检查（HaulAIUtility.cs:48）。
    /// 触发者：本 mod 的 GhostThing 不 Spawn 却挂进 listerThings.listsByDef
    /// （供其他 mod 扫描核心库存），原版 WorkGiver_CookFillHopper.HopperFillFoodJob
    /// 走 ThingsOfDef 遍历时拿到 ghost → PawnCanAutomaticallyHaul(ghost) → NRE。
    /// 语义：不在地图上的物品本来就不能 haul，提前返回 false 与原版意图一致，
    /// 顺带保护所有会把 despawned thing 放进索引的 mod。
    ///
    /// 守卫写成 t.Map == null（而不是 !t.Spawned）：两者等价（Thing.Map 仅在
    /// Spawned 时非 null，见 Thing.cs:233），但 t.Map == null 正是原版那行 NRE 的
    /// 充要条件，语义更准。**不要**改成 t.MapHeld == null —— 非 Fast 版读的是裸
    /// t.Map，容器内容物（未 Spawn 但 MapHeld 非 null）在这里本就走不通。
    /// </summary>
    [HarmonyPatch(typeof(HaulAIUtility), "PawnCanAutomaticallyHaul")]
    static class Patch_HaulAIUtility_PawnCanAutomaticallyHaul
    {
        static bool Prefix(Pawn p, Thing t, bool forced, ref bool __result)
        {
            if (p == null || t == null || t.Map == null)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// 同款防御：PawnCanAutomaticallyHaulFast_NewTemp 行 82 的 t.Fogged() 展开即
    /// <c>t.MapHeld.fogGrid.IsFogged(t.PositionHeld)</c>（GridsUtility.cs:86，无 Spawned 守卫），
    /// MapHeld 为 null 时 NRE。
    ///
    /// ⚠️ 守卫必须是 <b>t.MapHeld == null</b>，<b>不能</b>是 !t.Spawned。
    /// 容器内容物未 Spawn，但 `ParentHolder =&gt; Map` 数据修复让 MapHeld 非 null，
    /// 原版这条路径对它们完全安全 —— 而搬出路径正是走这里：
    ///   ListerHaulables.HaulSourcesCheckTick → Check(内容物) → ShouldBeHaulable 通过
    ///   → WorkGiver_Haul.JobOnThing:26 → PawnCanAutomaticallyHaulFast
    /// 用 !t.Spawned 会把内容物一律挡掉：容器里被过滤器排除的物品永远搬不出来
    /// （甲-1 静默失效，且只在主仓补丁与 4.0 数据层同时加载时才暴露）。
    /// </summary>
    [HarmonyPatch(typeof(HaulAIUtility), "PawnCanAutomaticallyHaulFast")]
    static class Patch_HaulAIUtility_PawnCanAutomaticallyHaulFast
    {
        static bool Prefix(Pawn p, Thing t, bool forced, ref bool __result)
        {
            if (p == null || t == null || t.MapHeld == null)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }
    // 作业排队后可能断电, 实际取出前仍需检查
    [HarmonyPatch(typeof(Toils_Haul), nameof(Toils_Haul.StartCarryThing))]
    internal static class Patch_StartCarryThing_CorePower
    {
        private static void Postfix(TargetIndex haulableInd, Toil __result)
        {
            __result.AddFailCondition(() =>
            {
                Thing target = __result.actor.CurJob.GetTarget(haulableInd).Thing;
                return target?.ParentHolder is Building_StorageCore core && !core.HaulSourceEnabled;
            });
        }
    }

    [HarmonyPatch(typeof(JobDriver_Wear), "MakeNewToils")]
    internal static class Patch_JobDriver_Wear_CorePower
    {
        private static IEnumerable<Toil> Postfix(IEnumerable<Toil> __result, JobDriver_Wear __instance)
        {
            foreach (Toil toil in __result)
            {
                toil.AddFailCondition(() =>
                {
                    Thing apparel = __instance.job.GetTarget(TargetIndex.A).Thing;
                    return apparel?.ParentHolder is Building_StorageCore core && !core.ApparelSourceEnabled;
                });
                yield return toil;
            }
        }
    }
}
