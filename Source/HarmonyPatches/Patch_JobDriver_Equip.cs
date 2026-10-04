using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 让 <see cref="JobDriver_Equip"/> 支持"武器放在通用容器里"。
    ///
    /// <para><b>原版的硬编码</b>（<c>Verse.AI/JobDriver_Equip.cs</c>）：
    /// <code>
    /// :15  TargetIsOnOutfitStand =&gt; target != null &amp;&amp; !target.Spawned
    ///                                &amp;&amp; target.ParentHolder is Building_OutfitStand   // 具体类
    /// :28  OutfitStand =&gt; (Building_OutfitStand)job.GetTarget(TargetIndex.B).Thing      // 硬转换
    /// :35  job.targetB = (Building_OutfitStand)Target.ParentHolder                        // 硬转换
    /// :55  pawn.Reserve((Building_OutfitStand)job.targetA.Thing.ParentHolder, ...)        // 硬转换
    /// </code>
    /// 所以武器一旦住在我们的容器里，<c>TargetIsOnOutfitStand</c> 为 false ⇒ 走 :101 的 else 分支
    /// ⇒ 对**未 Spawn 的物品**调 <c>DeSpawn()</c>（报错）⇒ 在物品**仍在容器里**时
    /// <c>pawn.equipment.AddEquipment(...)</c>。</para>
    ///
    /// <para><b>为什么原版只给衣架开了这条路</b>：<c>Building_OutfitStand</c> 的
    /// <c>defaultStorageSettings</c> 拒武器、只有 <c>fixedStorageSettings</c> 放行 ——
    /// 原版是用"不让武器进通用存储"来规避这个硬编码的。
    /// 注意 <c>JobDriver_Wear</c>（衣物）走的是**接口** <c>IApparelSource</c>，没有这个问题 ——
    /// 这是原版自己的不一致。</para>
    ///
    /// <para><b>本补丁的做法</b>：不跟那个迭代器较劲（改 5 个触点要写 transpiler），
    /// 直接 <c>Prefix</c> 掉 <c>MakeNewToils</c>，把"从容器取出并装备"这条链自己走一遍。
    /// 只在「目标是未 Spawn 且住在 IHaulSource 容器里、且容器不是衣架」时接管，
    /// 其余（地面武器 / 衣架武器）原样走原版。</para>
    ///
    /// <para><b>兼容</b>：<c>VEF.Apparels.JobDriver_EquipShield : JobDriver_Equip</c> 只覆盖了
    /// <c>TryMakePreToilReservations</c>，没有覆盖 <c>MakeNewToils</c> ⇒ 本前缀也作用于它，
    /// 但触发条件只认通用容器，不影响盾牌链接。卫语句保证对衣架零影响。</para>
    /// </summary>
    internal static class JobDriver_Equip_ContainerCompat
    {
        /// <summary>
        /// 目标是不是"住在通用 IHaulSource 容器里"。
        /// 排除 <c>Building_OutfitStand</c>：那条原版路径自带 <c>HeldWeapon</c> / <c>RemoveHeldWeapon</c> 语义，
        /// 交回原版处理。
        /// </summary>
        internal static Thing ContainerHolding(Job job)
        {
            if (job == null) return null;
            Thing target = job.GetTarget(TargetIndex.A).Thing;
            if (target == null || target.Spawned) return null;

            Thing holder = target.ParentHolder as Thing;
            if (holder == null) return null;
            if (holder is Building_OutfitStand) return null;
            if (!(holder is IHaulSource)) return null;
            return holder;
        }
    }

    /// <summary>让 <c>job.targetB</c> 指向容器，<c>MakeNewToils</c> 里的 GotoThing(B) 才有落脚点。</summary>
    [HarmonyPatch(typeof(JobDriver_Equip), "Notify_Starting")]
    internal static class Patch_JobDriver_Equip_NotifyStarting
    {
        [HarmonyPostfix]
        internal static void Postfix(JobDriver_Equip __instance)
        {
            Thing holder = JobDriver_Equip_ContainerCompat.ContainerHolding(__instance.job);
            if (holder != null) __instance.job.targetB = holder;
        }
    }

    /// <summary>
    /// 接管 toil 链。仅当目标是通用容器内容物时替换 <c>__result</c> 并跳过原版。
    /// </summary>
    [HarmonyPatch(typeof(JobDriver_Equip), "MakeNewToils")]
    internal static class Patch_JobDriver_Equip_MakeNewToils
    {
        [HarmonyPrefix]
        internal static bool Prefix(JobDriver_Equip __instance, ref IEnumerable<Toil> __result)
        {
            Thing holder = JobDriver_Equip_ContainerCompat.ContainerHolding(__instance.job);
            if (holder == null) return true;

            // 防御：万一没走 Notify_Starting（例如从存档恢复的旧作业），这里补上。
            if (!__instance.job.targetB.IsValid) __instance.job.targetB = holder;

            __result = ToilsFromContainer(__instance, holder);
            return false;
        }

        private static IEnumerable<Toil> ToilsFromContainer(JobDriver_Equip driver, Thing holder)
        {
            Job job = driver.job;
            Pawn pawn = driver.pawn;

            // 与原版 MakeNewToils 的第一段保持语义一致。
            Toil clearDropped = Toils_General.Do(delegate { pawn.mindState.droppedWeapon = null; });
            yield return clearDropped;

            // 走到容器那里（不是走到容器内容物 —— 内容物没有自己的格子）。
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(TargetIndex.B)
                .FailOn(() => holder is DigitalStorage.Components.Building_StorageCore core && !core.HaulSourceEnabled);

            Toil equip = ToilMaker.MakeToil("DS_EquipFromContainer");
            equip.initAction = delegate
            {
                if (holder is DigitalStorage.Components.Building_StorageCore core && !core.HaulSourceEnabled)
                {
                    driver.EndJobWith(JobCondition.Incompletable);
                    return;
                }
                Thing target = job.GetTarget(TargetIndex.A).Thing;
                ThingWithComps weapon = target as ThingWithComps;
                if (weapon == null || weapon.Destroyed)
                {
                    driver.EndJobWith(JobCondition.Errored);
                    return;
                }

                ThingOwner owner = (holder as IThingHolder)?.GetDirectlyHeldThings();
                if (owner == null || !owner.Contains(weapon))
                {
                    driver.EndJobWith(JobCondition.Errored);
                    return;
                }

                ThingWithComps taken;
                if (weapon.def.stackLimit > 1 && weapon.stackCount > 1)
                {
                    // 只取 1 个：ThingOwner.Take 会把拆出来的那份交给我们。
                    taken = owner.Take(weapon, 1) as ThingWithComps;
                }
                else
                {
                    // 整摞取走。物品**本来就没 Spawned**，所以这里**不要**调 DeSpawn()
                    // （原版那个 else 分支是给地面物品写的，对未 Spawn 物品调 DeSpawn 会报错）。
                    owner.Remove(weapon);
                    taken = weapon;
                }

                if (taken == null)
                {
                    driver.EndJobWith(JobCondition.Errored);
                    return;
                }

                pawn.equipment.MakeRoomFor(taken);
                pawn.equipment.AddEquipment(taken);
                taken.def.soundInteract?.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
            };
            equip.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return equip;
        }
    }
}
