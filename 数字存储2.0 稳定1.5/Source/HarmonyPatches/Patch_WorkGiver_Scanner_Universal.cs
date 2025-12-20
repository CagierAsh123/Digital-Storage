// 通用 WorkGiver Patch：自动支持需要物品的 Job
// 支持的 Job 类型：FeedPatient（喂饭）、TendPatient（医疗）、Refuel（装填）等
// 核心逻辑：在 WorkGiver 创建 Job 后，检测物品是否在核心中，如果是则补货+传送

using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Services;
using DigitalStorage.Settings;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 通用 WorkGiver Patch：自动支持需要从存储区拿物品的 Job
    /// </summary>
    [HarmonyPatch(typeof(WorkGiver_Scanner), "JobOnThing")]
    public static class Patch_WorkGiver_Scanner_Universal
    {
        // Job 配置：定义哪些 Job 需要支持，以及物品在哪个 target
        private static Dictionary<JobDef, JobTargetConfig> supportedJobs = new Dictionary<JobDef, JobTargetConfig>();

        // 初始化配置
        static Patch_WorkGiver_Scanner_Universal()
        {
            // 延迟初始化，等待 JobDefOf 加载
        }

        private static void InitializeConfigs()
        {
            if (supportedJobs.Count > 0) return;

            supportedJobs = new Dictionary<JobDef, JobTargetConfig>
            {
                // 给囚犯/病人喂饭：targetA = 食物
                { JobDefOf.FeedPatient, new JobTargetConfig 
                { 
                    ItemTarget = TargetIndex.A,
                    TargetPosition = TargetIndex.B,  // 病人位置
                    Description = "食物"
                }},
                
                // 医疗：targetB = 药品，targetA = 病人
                { JobDefOf.TendPatient, new JobTargetConfig 
                { 
                    ItemTarget = TargetIndex.B,
                    TargetPosition = TargetIndex.A,  // 病人位置
                    Description = "药品",
                    AllowNull = true  // 可以不用药治疗
                }},
                
                // 装填燃料：targetB = 燃料，targetA = 建筑
                { JobDefOf.Refuel, new JobTargetConfig 
                { 
                    ItemTarget = TargetIndex.B,
                    TargetPosition = TargetIndex.A,  // 建筑位置
                    Description = "燃料"
                }},
                
                // 批量装填：targetQueueB = 燃料列表
                { JobDefOf.RefuelAtomic, new JobTargetConfig 
                { 
                    UseQueue = true,
                    QueueTarget = TargetIndex.B,
                    TargetPosition = TargetIndex.A,  // 建筑位置
                    Description = "燃料（批量）"
                }},
                
                // 炮塔装填：targetB = 弹药
                { JobDefOf.RearmTurret, new JobTargetConfig 
                { 
                    ItemTarget = TargetIndex.B,
                    TargetPosition = TargetIndex.A,  // 炮塔位置
                    Description = "弹药"
                }},
                
                // 炮塔批量装填
                { JobDefOf.RearmTurretAtomic, new JobTargetConfig 
                { 
                    UseQueue = true,
                    QueueTarget = TargetIndex.B,
                    TargetPosition = TargetIndex.A,  // 炮塔位置
                    Description = "弹药（批量）"
                }},
            };
        }

        public static void Postfix(ref Job __result, Pawn pawn, Thing t, WorkGiver_Scanner __instance)
        {
            if (__result == null || pawn == null || pawn.Map == null)
            {
                return;
            }

            InitializeConfigs();

            // 检查 Job 是否在支持列表中
            if (!supportedJobs.TryGetValue(__result.def, out JobTargetConfig config))
            {
                return;
            }

            // 检查访问权限
            bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
            bool hasInterface = HasAccessibleInterface(pawn.Map);
            
            if (!hasChip && !hasInterface)
            {
                return; // 无访问权限
            }

            // 处理 Job
            if (config.UseQueue)
            {
                ProcessJobQueue(__result, pawn, config, hasChip, hasInterface);
            }
            else
            {
                ProcessJobSingleItem(__result, pawn, config, hasChip, hasInterface);
            }
        }

        /// <summary>
        /// 处理单个物品的 Job
        /// </summary>
        private static void ProcessJobSingleItem(Job job, Pawn pawn, JobTargetConfig config, bool hasChip, bool hasInterface)
        {
            Thing item = GetTargetThing(job, config.ItemTarget);
            
            // 允许 null（如不用药治疗）
            if (item == null)
            {
                if (config.AllowNull)
                {
                    return;
                }
                return;
            }

            // 检查物品是否在核心中
            Building_StorageCore core = FindCoreWithItem(item, pawn.Map);
            if (core == null)
            {
                return; // 物品不在核心中，使用原版逻辑
            }

            // 从虚拟存储补货
            int needed = job.count > 0 ? job.count : item.stackCount;
            Thing replenished = ReplenishItem(core, item.def, item.Stuff, needed, pawn.Map);
            
            if (replenished == null || !replenished.Spawned)
            {
                if (DigitalStorageSettings.enableDebugLog)
                {
                    Log.Warning($"[数字存储] WorkGiver补货失败: {config.Description}, Job={job.def.defName}");
                }
                return;
            }

            // 确定传送位置
            IntVec3? teleportPos = DetermineTeleportPosition(pawn, core, job, config, hasChip, hasInterface);
            
            // 传送
            if (teleportPos.HasValue && teleportPos.Value != replenished.Position)
            {
                replenished = TeleportItem(replenished, teleportPos.Value, pawn.Map);
                
                if (replenished == null)
                {
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Warning($"[数字存储] WorkGiver传送失败: {config.Description}");
                    }
                    return;
                }
            }

            // 更新 Job 的 target
            SetTargetThing(job, config.ItemTarget, replenished);

            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] WorkGiver补货成功: {config.Description}, {replenished.Label} x{replenished.stackCount}");
            }
        }

        /// <summary>
        /// 处理队列物品的 Job（RefuelAtomic）
        /// </summary>
        private static void ProcessJobQueue(Job job, Pawn pawn, JobTargetConfig config, bool hasChip, bool hasInterface)
        {
            List<LocalTargetInfo> queue = GetTargetQueue(job, config.QueueTarget);
            if (queue == null || queue.Count == 0)
            {
                return;
            }

            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return;
            }

            // 遍历队列，检查并补货
            for (int i = 0; i < queue.Count; i++)
            {
                if (!queue[i].HasThing || queue[i].Thing == null)
                {
                    continue;
                }

                Thing item = queue[i].Thing;
                Building_StorageCore core = FindCoreWithItem(item, pawn.Map);
                
                if (core == null)
                {
                    continue; // 物品不在核心中
                }

                // 补货
                Thing replenished = ReplenishItem(core, item.def, item.Stuff, item.stackCount, pawn.Map);
                
                if (replenished == null || !replenished.Spawned)
                {
                    continue;
                }

                // 确定传送位置
                IntVec3? teleportPos = DetermineTeleportPosition(pawn, core, job, config, hasChip, hasInterface);
                
                // 传送
                if (teleportPos.HasValue && teleportPos.Value != replenished.Position)
                {
                    replenished = TeleportItem(replenished, teleportPos.Value, pawn.Map);
                    
                    if (replenished == null)
                    {
                        continue;
                    }
                }

                // 更新队列中的 target
                queue[i] = new LocalTargetInfo(replenished);
            }
        }

        /// <summary>
        /// 确定传送位置
        /// </summary>
        private static IntVec3? DetermineTeleportPosition(Pawn pawn, Building_StorageCore core, Job job, JobTargetConfig config, bool hasChip, bool hasInterface)
        {
            // 获取目标位置（病人/建筑）
            IntVec3 targetPos = GetTargetPosition(job, config.TargetPosition, pawn.Map);
            
            if (hasChip)
            {
                // 有芯片：传送到目标位置
                return targetPos;
            }
            else if (hasInterface)
            {
                // 无芯片：比较接口和核心到目标的距离
                Building_OutputInterface nearestInterface = FindNearestOutputInterface(pawn.Map, core, targetPos);
                
                if (nearestInterface != null && nearestInterface.Spawned)
                {
                    float interfaceDistance = (nearestInterface.Position - targetPos).LengthHorizontalSquared;
                    float coreDistance = (core.Position - targetPos).LengthHorizontalSquared;
                    
                    if (interfaceDistance < coreDistance)
                    {
                        // 接口更近，传送到接口
                        return nearestInterface.Position;
                    }
                }
            }
            
            // 核心更近或无接口，不传送
            return null;
        }

        /// <summary>
        /// 从虚拟存储补货
        /// </summary>
        private static Thing ReplenishItem(Building_StorageCore core, ThingDef def, ThingDef stuff, int count, Map map)
        {
            if (core == null || def == null || count <= 0 || map == null)
            {
                return null;
            }

            // 检查预留物品
            Thing reserved = core.FindReservedItem(def, stuff);
            if (reserved != null && reserved.Spawned && reserved.stackCount >= count)
            {
                return reserved; // 预留物品足够
            }

            // 从虚拟存储提取
            int needMore = count - (reserved?.stackCount ?? 0);
            Thing extracted = core.ExtractItem(def, needMore, stuff);
            
            if (extracted == null)
            {
                return reserved; // 返回预留物品（如果有）
            }

            // 合并或生成
            if (reserved != null && reserved.Spawned)
            {
                reserved.TryAbsorbStack(extracted, false);
                if (extracted != null && !extracted.Destroyed && extracted.stackCount > 0)
                {
                    GenSpawn.Spawn(extracted, reserved.Position, map, WipeMode.Vanish);
                }
                return reserved;
            }
            else
            {
                GenSpawn.Spawn(extracted, core.Position, map, WipeMode.Vanish);
                return extracted;
            }
        }

        /// <summary>
        /// 传送物品
        /// </summary>
        private static Thing TeleportItem(Thing item, IntVec3 targetPos, Map map)
        {
            if (item == null || !item.Spawned || map == null)
            {
                return null;
            }

            item.DeSpawn(0);
            Thing spawned = GenSpawn.Spawn(item, targetPos, map, 0);
            FleckMaker.ThrowLightningGlow(spawned.DrawPos, map, 0.5f);
            
            return spawned;
        }

        /// <summary>
        /// 查找包含物品的核心
        /// </summary>
        private static Building_StorageCore FindCoreWithItem(Thing item, Map map)
        {
            if (item == null || map == null)
            {
                return null;
            }

            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null)
            {
                return null;
            }

            // 检查物品是否在核心的 SlotGroup 中
            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered || core.Map != map)
                {
                    continue;
                }

                SlotGroup slotGroup = core.GetSlotGroup();
                if (slotGroup != null && slotGroup.HeldThings.Contains(item))
                {
                    return core;
                }
            }

            return null;
        }

        /// <summary>
        /// 查找离目标最近的输出接口
        /// </summary>
        private static Building_OutputInterface FindNearestOutputInterface(Map map, Building_StorageCore core, IntVec3 targetPos)
        {
            if (map == null || core == null)
            {
                return null;
            }

            DigitalStorageMapComponent mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null)
            {
                return null;
            }

            Building_OutputInterface nearest = null;
            float nearestDist = float.MaxValue;

            foreach (Building_OutputInterface iface in mapComp.GetAllOutputInterfaces())
            {
                if (iface == null || !iface.Spawned || !iface.IsActive || iface.Map != map)
                {
                    continue;
                }

                if (iface.BoundCore != core)
                {
                    continue;
                }

                float dist = (iface.Position - targetPos).LengthHorizontalSquared;
                if (dist < nearestDist)
                {
                    nearestDist = dist;
                    nearest = iface;
                }
            }

            return nearest;
        }

        /// <summary>
        /// 检查是否有可访问的输出接口
        /// </summary>
        private static bool HasAccessibleInterface(Map map)
        {
            if (map == null)
            {
                return false;
            }

            DigitalStorageMapComponent mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp == null)
            {
                return false;
            }

            foreach (Building_OutputInterface iface in mapComp.GetAllOutputInterfaces())
            {
                if (iface != null && iface.Spawned && iface.IsActive && iface.BoundCore != null && iface.BoundCore.Powered)
                {
                    return true;
                }
            }

            return false;
        }

        // 辅助方法：获取 target
        private static Thing GetTargetThing(Job job, TargetIndex index)
        {
            switch (index)
            {
                case TargetIndex.A:
                    return job.targetA.HasThing ? job.targetA.Thing : null;
                case TargetIndex.B:
                    return job.targetB.HasThing ? job.targetB.Thing : null;
                case TargetIndex.C:
                    return job.targetC.HasThing ? job.targetC.Thing : null;
                default:
                    return null;
            }
        }

        private static void SetTargetThing(Job job, TargetIndex index, Thing thing)
        {
            switch (index)
            {
                case TargetIndex.A:
                    job.targetA = thing;
                    break;
                case TargetIndex.B:
                    job.targetB = thing;
                    break;
                case TargetIndex.C:
                    job.targetC = thing;
                    break;
            }
        }

        private static List<LocalTargetInfo> GetTargetQueue(Job job, TargetIndex index)
        {
            switch (index)
            {
                case TargetIndex.A:
                    return job.targetQueueA;
                case TargetIndex.B:
                    return job.targetQueueB;
                default:
                    return null;
            }
        }

        private static IntVec3 GetTargetPosition(Job job, TargetIndex index, Map map)
        {
            switch (index)
            {
                case TargetIndex.A:
                    if (job.targetA.HasThing && job.targetA.Thing != null)
                        return job.targetA.Thing.Position;
                    return job.targetA.Cell;
                case TargetIndex.B:
                    if (job.targetB.HasThing && job.targetB.Thing != null)
                        return job.targetB.Thing.Position;
                    return job.targetB.Cell;
                case TargetIndex.C:
                    if (job.targetC.HasThing && job.targetC.Thing != null)
                        return job.targetC.Thing.Position;
                    return job.targetC.Cell;
                default:
                    return IntVec3.Invalid;
            }
        }

        /// <summary>
        /// Job 配置类
        /// </summary>
        private class JobTargetConfig
        {
            public TargetIndex ItemTarget;      // 物品在哪个 target
            public TargetIndex TargetPosition;  // 目标位置（病人/建筑）
            public bool UseQueue;               // 是否使用队列
            public TargetIndex QueueTarget;     // 队列在哪个 target
            public string Description;          // 描述
            public bool AllowNull;              // 是否允许物品为 null
        }
    }
}
