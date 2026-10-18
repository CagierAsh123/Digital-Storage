using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using DigitalStorage.AI;
using DigitalStorage.Core;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 自动收纳的**开发者排查工具**：点地图格子 → 逐条打印"这里的某个东西为什么没被吸进核心"。
    ///
    /// <para><b>为什么需要它</b>：自动收纳的判定链很长，而且<b>大部分闸门是静默的</b> ——
    /// 不吸就是不吸，没有任何日志。这条链按执行顺序是：</para>
    ///
    /// <list type="number">
    /// <item><b>核心层</b>：核心存在 / `CompAutoIngest.Enabled` / 通电 / 设置里的总开关 / 研究 / 15 tick 相位</item>
    /// <item><b>候选层</b>：候选**只来自原版待搬表** `map.listerHaulables.ThingsPotentiallyNeedingHauling()`
    ///   —— 不在这张表里的东西我们**根本看不见**，而"为什么不在表里"由原版
    ///   `ListerHaulables.ShouldBeHaulable:194` 回答（四道门，全部静默）</item>
    /// <item><b>过滤层</b>：<see cref="CompAutoIngest.RejectReason"/>（不在图上 / 工作台材料区 / 作业目标 / 关押区 / 被预订 / 刚取出 / 工作台旁边宽限中）</item>
    /// <item><b>目的地层</b>：<see cref="CompAutoIngest.WouldVanillaHaulIntoCore"/> ——
    ///   走原版 `StoreUtility.TryFindBestBetterNonSlotGroupStorageFor`（`acceptSamePriority: false`！）
    ///   外加一条"格子型储存有同级或更高优先级就让给格子"的保护</item>
    /// </list>
    ///
    /// <para><b>本工具的原则：只报事实，不复刻判定。</b> 每一行都是**真的调用了那个原版方法**之后的结果
    /// （`ShouldBeHaulable` 的四道门是逐条问同一个 API，不是自己另写一套近似）——
    /// 本 mod 在诊断上已经吃过一次亏：另写一份近似判断会和真闸门漂移，制造"全绿但就是不工作"的假象。</para>
    /// </summary>
    internal static class AutoIngestDevTool
    {
        private const string Category = "DigitalStorage";

        private static readonly StringBuilder SB = new StringBuilder();

        // ===================================================================
        // 入口 1：点格子
        // ===================================================================

        [DebugAction(Category, "为何没被自动收纳（点格子）",
            actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DiagnoseCell()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;

            // ⚠️ 必须写全 `Verse.UI` —— 本 mod 自己有个 DigitalStorage.UI 命名空间，
            // 裸写 `UI.MouseCell()` 会被解析成 DigitalStorage.UI（编译期就报 CS0234）。
            IntVec3 cell = Verse.UI.MouseCell();
            if (!cell.InBounds(map)) return;

            // 点击反馈：光看日志窗口容易不知道点没点上
            MoteMaker.ThrowText(cell.ToVector3Shifted(), map, "DS 诊断 → 日志");

            SB.Length = 0;
            List<Thing> things = map.thingGrid.ThingsListAt(cell);

            SB.AppendLine("=== [DS 诊断] 格子 " + cell + "  共 " + things.Count + " 个 Thing ===");
            AppendGlobalState(map);

            if (things.Count == 0)
            {
                SB.AppendLine("  （这一格上没有任何 Thing）");
            }

            for (int i = 0; i < things.Count; i++)
            {
                AppendThing(map, cell, things[i], i);
            }

            SB.AppendLine("=== [DS 诊断] 结束 ===");
            Log.Warning(SB.ToString());
        }

        // ===================================================================
        // 入口 2：全图汇总（找"到底有哪些没收进去"）
        // ===================================================================

        [DebugAction(Category, "列出全图未被收纳的物品（汇总）",
            actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DumpAllUncollected()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;

            List<Building_StorageCore> cores = FindAllCores(map);
            if (cores.Count == 0)
            {
                Log.Warning("[DS 汇总] 这张图上没有任何存储核心。");
                return;
            }

            SB.Length = 0;
            SB.AppendLine("=== [DS 汇总] 本图未被自动收纳的可搬物 ===");
            AppendGlobalState(map);

            // 候选池 = 原版待搬表（自动收纳的唯一来源）
            List<Thing> pool = new List<Thing>();
            foreach (Thing t in map.listerHaulables.ThingsPotentiallyNeedingHauling())
            {
                if (t != null && t.Spawned) pool.Add(t);
            }
            SB.AppendLine("  原版待搬表里的东西：" + pool.Count + " 件");

            int blockedByFilter = 0, blockedByDest = 0, ok = 0, listed = 0;
            for (int i = 0; i < pool.Count; i++)
            {
                Thing t = pool[i];
                CompAutoIngest.Reject rej = CompAutoIngest.RejectReason(t, map);
                if (rej != CompAutoIngest.Reject.None)
                {
                    blockedByFilter++;
                    if (listed < 40) { listed++; SB.AppendLine("  [过滤] " + Brief(t) + " @ " + t.PositionHeld + " → " + RejText(rej)); }
                    continue;
                }
                Building_StorageCore dest = CompAutoIngest.WouldVanillaHaulIntoCore(map, t);
                if (dest == null)
                {
                    blockedByDest++;
                    if (listed < 40) { listed++; SB.AppendLine("  [目的地] " + Brief(t) + " @ " + t.PositionHeld + " → " + DestReason(map, t, cores)); }
                    continue;
                }
                ok++;
            }

            SB.AppendLine("  ---- 小结 ----");
            SB.AppendLine("  过滤层挡下：" + blockedByFilter + " / 目的地层挡下：" + blockedByDest + " / 应被吸入：" + ok);
            if (listed >= 40) SB.AppendLine("  （明细已截断到 40 行）");
            SB.AppendLine("=== [DS 汇总] 结束 ===");
            Log.Warning(SB.ToString());
        }

        // ===================================================================
        // 入口 3：立刻对格子跑一次真实收纳（决定性的那一步）
        // ===================================================================

        /// <summary>
        /// 对格子上的每件东西**真的**重放一次 <c>CompTick</c> 的最后一跳，并打印每一步的结果。
        ///
        /// <para>与"点格子诊断"的区别：诊断是**只读**的（只问闸门），这个是**动手**的
        /// （真的调 <c>CompAutoIngest.TryIngest</c>）。所以它能一次分清两件事：</para>
        /// <list type="bullet">
        /// <item>调用后东西**进核心了** ⇒ 说明判定全通过，玩家的症状纯粹是"排不到队"（看 ⑤ 队列深度）</item>
        /// <item>调用后东西**还在原地** ⇒ 说明有一道闸门在只读诊断里看不出来，报出来的那一步就是它</item>
        /// </list>
        /// </summary>
        [DebugAction(Category, "立刻收纳这一格（动手，打印每一步）",
            actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ForceIngestCell()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;

            IntVec3 cell = Verse.UI.MouseCell();
            if (!cell.InBounds(map)) return;

            MoteMaker.ThrowText(cell.ToVector3Shifted(), map, "DS 强制收纳 → 日志");

            // ⚠️ 必须**先快照**：TryIngest 会 DeSpawn 物品，边遍历边改会炸
            List<Thing> things = new List<Thing>(map.thingGrid.ThingsListAt(cell));
            SB.Length = 0;
            SB.AppendLine("=== [DS 强制收纳] 格子 " + cell + "  共 " + things.Count + " 个 Thing ===");

            List<Building_StorageCore> cores = FindAllCores(map);
            if (cores.Count == 0)
            {
                SB.AppendLine("  本图没有核心，无从下手。");
                SB.AppendLine("=== [DS 强制收纳] 结束 ===");
                Log.Warning(SB.ToString());
                return;
            }

            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t == null) continue;

                SB.Append("  " + Brief(t) + " → ");

                CompAutoIngest.Reject rej = CompAutoIngest.RejectReason(t, map);
                if (rej != CompAutoIngest.Reject.None)
                {
                    SB.AppendLine("✗ 过滤层挡下：" + RejText(rej));
                    continue;
                }

                Building_StorageCore dest = CompAutoIngest.WouldVanillaHaulIntoCore(map, t);
                if (dest == null)
                {
                    SB.AppendLine("✗ 目的地层挡下：" + DestReason(map, t, cores));
                    continue;
                }

                bool ok = CompAutoIngest.TryIngest(dest, t);
                if (ok)
                {
                    SB.AppendLine("✓ **已吸进** " + dest.LabelCap + " @ " + dest.Position
                                  + "（⇒ 判定全通过，症状是排不到队，去看 ⑤ 队列深度）");
                }
                else
                {
                    SB.AppendLine("✗ TryIngest 返回 false —— 判定通过但落地失败。"
                                  + " Accepts=" + dest.Accepts(t)
                                  + "（注意：本函数**不会**刷新 @ 里的判定，因为东西已经被放回原处）");
                }
            }

            SB.AppendLine("=== [DS 强制收纳] 结束 ===");
            Log.Warning(SB.ToString());
        }

        // ===================================================================
        // 入口 4：点殖民者 → 它现在在等什么 / 该不该有活
        // ===================================================================

        /// <summary>
        /// 点一个 pawn，打印它**现在这个 job 的全部可读状态** + 本图建造活的有无，
        /// 用来回答"它为什么站着等"。
        ///
        /// <para><b>为什么要这一条</b>：用户实测"有时（不是 100%）pawn 完成建造工作后进入几秒等待 job"。
        /// 这类问题的分支很多（没活 / 有活但没派到它 / 派了活但瞬间放弃 / 排队等材料），
        /// 而**能一次分清它们的只有两样东西**：当前 job 的 toil 与 job 事件序列。
        /// 前者这条工具直接打；后者用第二个动作打开原版的 <c>Pawn_JobTracker.debugLog</c>
        /// （原版自带的逐事件日志，会写进 Player.log）。</para>
        /// </summary>
        [DebugAction(Category, "这个 pawn 在等什么（点 pawn）",
            actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DiagnosePawn(Pawn p)
        {
            if (p == null) return;
            Map map = p.Map;
            if (map == null) return;

            MoteMaker.ThrowText(p.DrawPos, map, "DS pawn 诊断 → 日志");

            SB.Length = 0;
            SB.AppendLine("=== [DS pawn] " + p.LabelShort + "  " + p.def.defName
                          + "  阵营=" + (p.Faction == null ? "无" : p.Faction.Name)
                          + "  倒地=" + p.Downed + "  死亡=" + p.Dead + " ===");

            Job cur = p.CurJob;
            if (cur == null)
            {
                SB.AppendLine("  当前 job = null（**它现在没有任何工作** —— 原版会在下一个思考周期给它派活）");
            }
            else
            {
                SB.AppendLine("  当前 job = " + cur.def.defName + "   「" + cur.GetReport(p) + "」");
                SB.AppendLine("    targetA=" + cur.targetA + " / B=" + cur.targetB + " / C=" + cur.targetC
                              + " / count=" + cur.count
                              + " / targetQueueB=" + (cur.targetQueueB == null ? 0 : cur.targetQueueB.Count));
                if (cur.bill != null) SB.AppendLine("    bill = " + cur.bill.Label);
            }

            JobDriver drv = p.jobs == null ? null : p.jobs.curDriver;
            if (drv != null)
            {
                SB.AppendLine("  driver = " + drv.GetType().Name
                              + "  ended=" + drv.ended
                              + "  toils[" + drv.CurToilIndex + "] = " + drv.CurToilString);
            }
            SB.AppendLine("  jobs.debugLog = " + (p.jobs != null && p.jobs.debugLog)
                          + "（点「切换这个 pawn 的 job 调试日志」可打开，日志里就会有逐事件序列）");
            if (p.jobs != null && p.jobs.jobQueue != null && p.jobs.jobQueue.Count > 0)
            {
                SB.Append("  排队中的 job（" + p.jobs.jobQueue.Count + "）：");
                for (int i = 0; i < p.jobs.jobQueue.Count; i++) SB.Append(p.jobs.jobQueue[i].job.def.defName + " ");
                SB.AppendLine();
            }

            // 工作类型是否启用 —— "没活干"最常见的其实是"这个工作类型被关了"
            if (p.workSettings != null && p.workSettings.EverWork)
            {
                SB.AppendLine("  工作类型：建造=" + p.workSettings.GetPriority(WorkTypeDefOf.Construction)
                              + " / 搬运=" + p.workSettings.GetPriority(WorkTypeDefOf.Hauling)
                              + "  （0 = 关闭）");
            }
            else
            {
                SB.AppendLine("  工作类型：workSettings 不可用（非玩家派系 / 机械体 / 无工作能力）");
            }

            // 为什么它拿不到活：机械体身份 / 工作模式 / 领主 duty / 当前 job 的指纹
            AppendThinkTreeState(p);

            // 它真正的活源：原版待搬表（表里没有的东西谁也搬不走）
            AppendHaulWorkload(map, p);

            // 本图工地盘点：有活却没派给它，还是压根没活
            AppendConstructionWorkload(map, p);

            SB.AppendLine("=== [DS pawn] 结束 ===");
            Log.Warning(SB.ToString());
        }

        /// <summary>
        /// <b>为什么这个 pawn 拿不到活</b>：把 Ancot 无人机思考树（<c>Ancot_Drone</c>）上决定走哪条分支的
        /// 每个字段都打出来。
        ///
        /// <para><b>树的分支顺序</b>（<c>Ancot Library/1.6/Defs/ThinkTreeDefs/Drone.xml</c>）：
        /// 低电量自停 → 排队 job → 征召 → <b>领主职责（LordDuty）</b> → 玩家机械体〔工作模式 = 工作 时才有
        /// <c>JobGiver_Work</c>〕→ 非玩家机械体战斗 → 玩家机械体闲逛 → 闲逛 → <c>JobGiver_IdleError</c>。</para>
        ///
        /// <para><b>三个字段决定它走哪条</b>：<c>RaceProps.IsMechanoid</c>（配合阵营算出
        /// <c>IsColonyMech</c>，而 <c>ThinkNode_ConditionalPlayerMech</c> 判的就是它 ——
        /// 不是殖民地机械体就<b>永远到不了 <c>JobGiver_Work</c></b>）、<c>mindState.duty</c>
        /// （有 duty 就被<b>排在 JobGiver_Work 前面</b>的领主分支先接管）、
        /// <c>CompDrone.workMode</c>（Ancot 的工作模式：工作 / 充电 / 休眠）。</para>
        ///
        /// <para><b>「一直 Wait」在这棵树里只有三个来源，各有指纹</b>：</para>
        /// <list type="number">
        /// <item><c>JobGiver_IdleError</c> —— <c>expiryInterval = 100</c>，并在日志里留一行
        ///   <c>... issued IdleError wait job. The behavior tree should never get here.</c>
        ///   ⇒ 说明它前面<b>每一个</b>节点都没给出 job（含闲逛那份）；</item>
        /// <item>领主 duty 里的 <c>JobGiver_Idle</c> —— <c>expiryInterval</c> 是那个 DutyDef 的 ticks
        ///   （不是 100）；</item>
        /// <item>派活出错之后的恢复等待 —— <c>expiryInterval</c> 是 <b>250</b>
        ///   （<c>Pawn_JobTracker</c> 的 Errored / ErroredPather）或 <b>150</b>
        ///   （<c>JobUtility.StartErrorRecoverJob</c>），日志里有那一次的异常栈。</item>
        /// </list>
        /// </summary>
        private static void AppendThinkTreeState(Pawn p)
        {
            SB.AppendLine("  —— 它能不能拿到活（思考树分支）——");
            try
            {
                SB.AppendLine("    IsMechanoid=" + p.RaceProps.IsMechanoid
                              + "  IsColonyMech=" + p.IsColonyMech
                              + "  IsColonyMechPlayerControlled=" + p.IsColonyMechPlayerControlled
                              + "  精神状态=" + (p.MentalStateDef == null ? "无" : p.MentalStateDef.defName)
                              + "  Overseer=" + (p.GetOverseer() == null ? "无" : p.GetOverseer().LabelShort));
                SB.AppendLine("    征召中=" + p.Drafted
                              + "  正在移动=" + ((p.pather == null) ? "?" : p.pather.Moving.ToString())
                              + "  行动能力=" + p.health.capacities.CapableOf(PawnCapacityDefOf.Moving)
                              + "  可站立格=" + p.PositionHeld.Standable(p.MapHeld));
                if (!p.RaceProps.IsMechanoid || !p.IsColonyMech)
                {
                    SB.AppendLine("    ⚠ 它不是「殖民地机械体」⇒ Drone.xml 里那条玩家机械体分支"
                                  + "（含 JobGiver_Work）整条被跳过，它做不了任何工作（只剩战斗 / 闲逛）。");
                }
            }
            catch (Exception e) { SB.AppendLine("    身份判定异常：" + e.GetType().Name); }

            ThingComp drone = FindDroneComp(p);
            if (drone == null)
            {
                SB.AppendLine("    工作模式：这个 pawn 身上没有 AncotLibrary.CompDrone（不是 Ancot 无人机系）");
            }
            else
            {
                object mode;
                object initial;
                try
                {
                    mode = FieldValue(drone, "workMode");
                    initial = FieldValue(PropValue(drone, "Props"), "initialWorkMode");
                    SB.AppendLine("    工作模式 CompDrone.workMode = " + DefName(mode)
                                  + "（出厂值 " + DefName(initial) + "）"
                                  + "  depleted=" + FieldValue(drone, "depleted")
                                  + "  initialized=" + FieldValue(drone, "initialized"));
                    SB.AppendLine("    电量 PowerTicksLeft=" + PropValue(drone, "PowerTicksLeft")
                                  + "  PercentFull=" + PropValue(drone, "PercentFull")
                                  + "  IsSelfShutdown=" + PropValue(drone, "IsSelfShutdown"));
                    if (mode == null)
                    {
                        SB.AppendLine("    ⚠ workMode 为 null ⇒ 三个「工作模式」分支全不匹配，"
                                      + "它会一直掉到闲逛 / IdleError。这是 Ancot 无人机最典型的「不动」原因。");
                    }
                    else if (DefName(mode) != "Ancot_Work")
                    {
                        SB.AppendLine("    ⚠ 当前不是 Ancot_Work（工作）模式 ⇒ 它按设计就不干活（充电 / 休眠）。");
                    }
                }
                catch (Exception e) { SB.AppendLine("    工作模式读取异常：" + e.GetType().Name); }
            }

            try
            {
                Lord lord = p.GetLord();
                PawnDuty duty = (p.mindState == null) ? null : p.mindState.duty;
                SB.AppendLine("    领主 lord = " + ((lord == null)
                    ? "无"
                    : lord.LordJob.GetType().Name
                      + " / toil=" + ((lord.CurLordToil == null) ? "null" : lord.CurLordToil.GetType().Name)
                      + " / 在本 toil 已 " + lord.ticksInToil + " tick / 成员 " + lord.ownedPawns.Count));
                SB.AppendLine("    职责 mindState.duty = " + ((duty == null) ? "无" : duty.def.defName));
                if (duty != null)
                {
                    SB.AppendLine("    ⚠ 有 duty ⇒ Drone.xml 里**领主分支排在 JobGiver_Work 前面**，"
                                  + "它先被领主接管；领主不给活才轮到工作 / 闲逛。");
                }
            }
            catch (Exception e) { SB.AppendLine("    领主/duty 读取异常：" + e.GetType().Name); }

            Job cur = p.CurJob;
            if (cur != null)
            {
                SB.AppendLine("    job 指纹：" + cur.def.defName
                              + " / driver=" + ((cur.def.driverClass == null) ? "null" : cur.def.driverClass.Name)
                              + " / expiryInterval=" + cur.expiryInterval
                              + " / checkOverrideOnExpire=" + cur.checkOverrideOnExpire
                              + " / overrideFacing=" + cur.overrideFacing
                              + " / workGiver=" + ((cur.workGiverDef == null) ? "无" : cur.workGiverDef.defName));
                if (cur.def == JobDefOf.Wait)
                {
                    if (cur.expiryInterval == 100)
                    {
                        SB.AppendLine("       ⇒ expiryInterval=100 = **JobGiver_IdleError**：它前面的每个节点都没给出 job。"
                                      + "去 Player.log 搜「issued IdleError wait job」。");
                    }
                    else if (cur.expiryInterval == 250)
                    {
                        SB.AppendLine("       ⇒ expiryInterval=250 = 上一件活以 Errored / ErroredPather 收场后的恢复等待："
                                      + "日志里那次的异常栈才是根因。");
                    }
                    else if (cur.expiryInterval == 150)
                    {
                        SB.AppendLine("       ⇒ expiryInterval=150 = JobUtility.StartErrorRecoverJob：派活时抛异常了"
                                      + "（日志里那次的异常栈才是根因）。");
                    }
                    else
                    {
                        SB.AppendLine("       ⇒ 不是 100 / 250 / 150 ⇒ 多半来自**领主 duty 的 JobGiver_Idle**（看上面的 duty）。");
                    }
                }
            }

            try
            {
                if (p.mindState != null)
                {
                    SB.AppendLine("    mindState.IsIdle=" + p.mindState.IsIdle
                                  + "  lastJobTag=" + p.mindState.lastJobTag);
                }
                Area area = (p.playerSettings == null) ? null : p.playerSettings.AreaRestrictionInPawnCurrentMap;
                SB.AppendLine("    活动区 = " + ((area == null) ? "无限制" : area.Label)
                              + ((area == null) ? "" : "  它在区内=" + area[p.PositionHeld]));
                if (p.needs != null && p.needs.energy != null)
                {
                    SB.AppendLine("    机械体能量 needs.energy = " + p.needs.energy.CurLevelPercentage.ToStringPercent());
                }
            }
            catch (Exception e) { SB.AppendLine("    状态读取异常：" + e.GetType().Name); }
        }

        /// <summary>
        /// <b>它真正的活源</b>：原版 <c>WorkGiver_Haul</c> 的候选**只有**
        /// <c>map.listerHaulables.ThingsPotentiallyNeedingHauling()</c>。
        ///
        /// <para>这张表空了 ⇒ 全殖民地（含无人机）都没得搬。而本 mod 的「算中即入」正是让地表常年没有待搬物
        /// —— 那是<b>设计</b>，不是卡死：它站着不动是因为「没有活」，不是因为「拿不到活」。
        /// 这两件事在日志里长得一模一样，所以必须分开量。</para>
        /// </summary>
        private static void AppendHaulWorkload(Map map, Pawn p)
        {
            ICollection<Thing> haulables;
            try
            {
                haulables = map.listerHaulables.ThingsPotentiallyNeedingHauling();
            }
            catch (Exception e)
            {
                SB.AppendLine("  —— 搬运活盘点 ——");
                SB.AppendLine("      原版待搬表读取失败：" + e.GetType().Name);
                return;
            }
            if (haulables == null)
            {
                SB.AppendLine("  —— 搬运活盘点 ——");
                SB.AppendLine("      原版待搬表 = null");
                return;
            }

            SB.AppendLine("  —— 搬运活盘点 ——");
            SB.AppendLine("      原版待搬表 listerHaulables = " + haulables.Count + " 件"
                          + "（WorkGiver_Haul 的候选**只有**这一张表里的东西）");

            WorkGiver_Scanner haulWg = null;
            WorkGiverDef haulDef = DefDatabase<WorkGiverDef>.GetNamed("Haul", false);
            if (haulDef != null) haulWg = haulDef.Worker as WorkGiver_Scanner;

            int shown = 0;
            foreach (Thing t in haulables)
            {
                if (t == null) continue;
                if (shown >= 3) break;
                shown++;
                string note;
                bool has = false;
                try
                {
                    if (haulWg != null) has = haulWg.HasJobOnThing(p, t, false);
                    note = "  CanReserve=" + p.CanReserve(t)
                           + "  CanReach=" + p.CanReach(t, PathEndMode.ClosestTouch, Danger.Deadly)
                           + "  已被订=" + map.reservationManager.IsReservedByAnyoneOf(t, p.Faction);
                }
                catch (Exception e) { note = "  判定异常：" + e.GetType().Name; }
                SB.AppendLine("      · " + ((t.def == null) ? "?" : t.def.defName) + " @" + t.PositionHeld
                              + "  HasJobOnThing=" + has + note);
            }

            if (haulables.Count == 0)
            {
                SB.AppendLine("      ⇒ 表是空的：**没有搬运活可派**。地表物品已被核心收走（算中即入），"
                              + "它不动是正确行为 —— 想让它干活就得有框架 / 蓝图（我们的「从核心取料送工地」）"
                              + "或者别把东西全收进核心。");
            }
        }

        /// <summary>
        /// 找 Ancot 的 <c>CompDrone</c>（<b>不引用</b> AncotLibrary —— 那是软依赖）：
        /// 按类型沿继承链找名字为 <c>CompDrone</c> 的 comp（Milira 的子类 <c>CompDrone_Milira</c> 也命中）。
        /// </summary>
        private static ThingComp FindDroneComp(Pawn p)
        {
            List<ThingComp> comps = p.AllComps;
            if (comps == null) return null;
            for (int i = 0; i < comps.Count; i++)
            {
                ThingComp c = comps[i];
                if (c == null) continue;
                for (Type t = c.GetType(); t != null; t = t.BaseType)
                {
                    if (t.Name == "CompDrone") return c;
                }
            }
            return null;
        }

        private static object FieldValue(object obj, string name)
        {
            if (obj == null) return null;
            for (Type t = obj.GetType(); t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (f != null) return f.GetValue(obj);
            }
            return null;
        }

        private static object PropValue(object obj, string name)
        {
            if (obj == null) return null;
            for (Type t = obj.GetType(); t != null; t = t.BaseType)
            {
                PropertyInfo pr = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (pr != null && pr.CanRead) return pr.GetValue(obj, null);
            }
            return null;
        }

        private static string DefName(object obj)
        {
            if (obj == null) return "null";
            Def d = obj as Def;
            return (d != null) ? d.defName : obj.ToString();
        }

        /// <summary>
        /// 盘一下本图还有多少建造活，以及我们的取料工作能不能接手。
        /// "有活" + "它就是不去" ⇒ 问题在工作分配；"没活" ⇒ 站着等是正常的。
        /// </summary>
        private static void AppendConstructionWorkload(Map map, Pawn p)
        {
            List<Thing> frames = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame);
            List<Thing> blueprints = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint);
            SB.AppendLine("  本图工地：框架 " + frames.Count + " / 蓝图 " + blueprints.Count);

            int canMake = 0;
            int cap = 12;
            for (int i = 0; i < frames.Count && i < cap; i++)
            {
                IConstructible c = frames[i] as IConstructible;
                if (c != null && AI.DSConstructionDelivery.CanMakeJob(p, c, false)) canMake++;
            }
            for (int i = 0; i < blueprints.Count && i < cap; i++)
            {
                IConstructible c = blueprints[i] as IConstructible;
                if (c != null && AI.DSConstructionDelivery.CanMakeJob(p, c, false)) canMake++;
            }
            SB.AppendLine("  我们的「从核心取料送工地」现在能接手的（前 " + cap + " 个里数）：" + canMake + " 个");
            if (canMake > 0)
                SB.AppendLine("     ⇒ 有活。若它就是站着不干，看上面工作类型里「建造 / 搬运」的优先级是不是 0，");
            if (canMake > 0)
                SB.AppendLine("       或者它刚从这个工作类型上被踢下来（把 debugLog 打开看事件序列）。");
        }

        /// <summary>
        /// 打开/关闭原版自带的**逐 job 事件日志**（<c>Pawn_JobTracker.debugLog</c>）。
        /// 打开后这个 pawn 的每次 StartJob / EndCurrentJob（带 JobCondition 和当时的 toil）
        /// 都会写进 <c>Player.log</c> —— 抓"几秒等待"的完整因果链靠它。
        /// </summary>
        [DebugAction(Category, "切换这个 pawn 的 job 调试日志（点 pawn）",
            actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void ToggleJobDebugLog(Pawn p)
        {
            if (p == null || p.jobs == null) return;
            p.jobs.debugLog = !p.jobs.debugLog;
            if (p.Map != null)
                MoteMaker.ThrowText(p.DrawPos, p.Map, "DS job日志 " + (p.jobs.debugLog ? "开" : "关"));
            Log.Warning("[DS] " + p.LabelShort + " 的 job 调试日志 = " + p.jobs.debugLog);
        }

        // ===================================================================
        // A. 全局状态
        // ===================================================================

        private static void AppendGlobalState(Map map)
        {
            SB.AppendLine("--- 核心层 ---");
            SB.AppendLine("  设置·自动收纳总开关 autoIngestEnabled = " + DigitalStorage.Settings.DigitalStorageSettings.autoIngestEnabled);
            SB.AppendLine("  设置·避让作业 autoIngestYieldToJobs = " + DigitalStorage.Settings.DigitalStorageSettings.autoIngestYieldToJobs
                          + "（关 = 算中即入：『作业目标 / 工作台旁边宽限』那两条闸门不参与判定）");
            SB.AppendLine("  兼容·Pick Up And Haul 在场 = " + Compatibility.PickUpAndHaulCompat.IsActive
                          + " / 作业目标让位实际生效 = " + Compatibility.PickUpAndHaulCompat.YieldToJobTargets
                          + "（PUAH 在场时强制生效：它一趟捡多堆，被瞬移走就白跑）");
            SB.AppendLine("  研究·DigitalStorage_AutoIngest1 = " + ResearchDone("DigitalStorage_AutoIngest1")
                          + " / 2 = " + ResearchDone("DigitalStorage_AutoIngest2")
                          + " / 3 = " + ResearchDone("DigitalStorage_AutoIngest3"));

            List<Building_StorageCore> cores = FindAllCores(map);
            if (cores.Count == 0)
            {
                SB.AppendLine("  ⚠ 本图没有任何存储核心 → 什么都没得吸。");
                return;
            }

            for (int i = 0; i < cores.Count; i++)
            {
                Building_StorageCore c = cores[i];
                CompAutoIngest comp = c.GetComp<CompAutoIngest>();
                SB.Append("  [" + i + "] " + c.LabelCap + " @ " + c.Position
                          + "  通电=" + c.Powered
                          + " 出库=" + c.HaulSourceEnabled
                          + " 入库=" + c.HaulDestinationEnabled);
                SB.Append(" AutoIngest组件=" + (comp != null));
                if (comp != null) SB.Append(" 开关=" + comp.Enabled);
                SB.Append(" 优先级=" + c.GetStoreSettings().Priority);
                SB.Append(" 堆=" + c.innerContainer.Count + "/" + c.maxStacks
                          + "(Lv" + DigitalStorage.Core.CoreTier.Level + ")");
                SB.Append(" 兼容替身=" + c.CompatSlotGroupRegistered);
                SB.AppendLine();
            }
        }

        // ===================================================================
        // B. 逐件
        // ===================================================================

        private static void AppendThing(Map map, IntVec3 cell, Thing t, int index)
        {
            SB.AppendLine("--- #" + index + " " + Brief(t) + " ---");
            if (t == null) { SB.AppendLine("  （null）"); return; }

            // ① 候选层：在原版待搬表里吗？
            bool inPool = map.listerHaulables.ThingsPotentiallyNeedingHauling().Contains(t);
            SB.AppendLine("  ① 原版待搬表 listerHaulables：" + (inPool ? "在" : "★不在"));
            if (!inPool)
            {
                string why = HaulableGateReason(t, map);
                SB.AppendLine("     不在的原因（逐条问的原版 API）：" + (why == null ? "（四道门都不拦，应该在表里 —— 可能是增量维护的滞后，等 1 秒再看）" : why));
                SB.AppendLine("     ⇒ 自动收纳的候选**只来自这张表**，不在表里 = 我们根本看不见它。");
            }

            // ② 过滤层
            CompAutoIngest.Reject rej = CompAutoIngest.RejectReason(t, map);
            SB.AppendLine("  ② 我们的过滤 RejectReason = " + rej + (rej == CompAutoIngest.Reject.None ? "" : "（" + RejText(rej) + "）"));

            // ③ 当前位置 / 优先级
            IHaulDestination curDest = StoreUtility.CurrentHaulDestinationOf(t);
            StoragePriority curPrio = StoreUtility.CurrentStoragePriorityOf(t);
            SlotGroup sg = map.haulDestinationManager.SlotGroupAt(t.PositionHeld);
            SB.AppendLine("  ③ 现在在哪：" + (curDest == null ? "裸地（无储存归属）" : TypeName(curDest) + " / " + curDest.GetStoreSettings().Priority)
                          + "；CurrentStoragePriority = " + curPrio
                          + "；所在格子储存组 = " + (sg == null ? "无" : sg.Settings.Priority.ToString()));
            SB.AppendLine("     IsInAnyStorage=" + t.IsInAnyStorage() + " / IsInValidBestStorage=" + t.IsInValidBestStorage()
                          + " / IsForbidden(玩家)=" + t.IsForbidden(Faction.OfPlayer)
                          + " / EverHaulable=" + t.def.EverHaulable + " / alwaysHaulable=" + t.def.alwaysHaulable);

            // ④ 目的地层
            List<Building_StorageCore> cores = FindAllCores(map);
            Building_StorageCore dest = CompAutoIngest.WouldVanillaHaulIntoCore(map, t);
            SB.AppendLine("  ④ 原版会不会把它搬进核心：" + (dest == null ? "★不会" : "会 → " + dest.LabelCap + " @ " + dest.Position));
            if (dest == null)
            {
                SB.AppendLine("     原因：" + DestReason(map, t, cores));
            }

            // ⑤ 队列深度：四层全过也可能"永远轮不到"（见 AppendQueueDepth 的注释）
            AppendQueueDepth(map, t, cores);

            // ⑥ 结论
            SB.AppendLine("  ⑥ 结论：" + Verdict(t, inPool, rej, dest, cores));
        }

        /// <summary>
        /// <b>队列深度</b>：算出这件东西在"本图所有**可吸**物品"里排第几，本轮轮不轮得到。
        ///
        /// <para><b>为什么需要这一层</b>：自动收纳每 15 tick 只吸 <c>rate</c>（研究满级 = 10）件，
        /// 缓冲上限 <c>bufSize = rate * 3</c>。所以"四层全通过"只说明**它被检查时会通过**，
        /// 不等于**它被检查过**。2026-10-02 就踩了这个：缓冲只装"通过过滤"的，
        /// 而"原版不会搬进核心"的东西也占槽位 ⇒ 表头长期压着 30 件这种东西，
        /// 排在后面的物品永远进不了缓冲，而单件诊断每次都说"四层全通过"。
        /// 现在收集阶段就判目的地（缓冲只装可吸的）并加了轮转扫描；
        /// 这一层用来回答剩下的正常排队问题："还要等几轮"。</para>
        ///
        /// <para><b>这一层是复刻而非调用</b>：那段逻辑写在 <c>CompTick</c> 方法体里，没有可调用的函数。
        /// 复刻时严格对齐三件事：候选来源（待搬表）、过滤（<c>RejectReason == None</c>）、
        /// 目的地判定（<c>WouldVanillaHaulIntoCore</c>）。</para>
        /// </summary>
        private static void AppendQueueDepth(Map map, Thing t, List<Building_StorageCore> cores)
        {
            if (cores.Count == 0 || t == null) return;

            CompAutoIngest comp = cores[0].GetComp<CompAutoIngest>();
            int rate = (comp != null) ? comp.IngestRate : 1;
            int bufSize = rate * 3;

            ICollection<Thing> pool = map.listerHaulables.ThingsPotentiallyNeedingHauling();
            int position = -1;
            int ingestable = 0;
            int scanned = 0;
            const int ScanCap = 800; // 诊断是一次性点击，给宽一点；防超大表卡住 UI

            foreach (Thing x in pool)
            {
                if (scanned++ >= ScanCap) break;
                if (x == null || x.Destroyed) continue;
                if (CompAutoIngest.RejectReason(x, map) != CompAutoIngest.Reject.None) continue;
                if (CompAutoIngest.WouldVanillaHaulIntoCore(map, x) == null) continue;

                if (ReferenceEquals(x, t)) { position = ingestable; }
                ingestable++;
            }

            SB.AppendLine("  ⑤ 队列深度（每 " + 15 + " tick 一轮）：待搬表 " + pool.Count + " 件"
                          + " / 其中可吸 " + ingestable + " 件"
                          + " / 每轮上限 rate=" + rate + "、缓冲 bufSize=" + bufSize
                          + " / 轮转起点=" + CompAutoIngest.ScanOffset);

            if (position < 0)
            {
                SB.AppendLine("     目标不在这 " + ScanCap + " 件的可吸集合里（可能它自己不可吸，见 ①~④）");
            }
            else if (position < rate)
            {
                SB.AppendLine("     目标位次 = " + position + "（< rate）⇒ 本轮就该轮到它。若持续不吸请把这整段发我。");
            }
            else if (position < bufSize)
            {
                SB.AppendLine("     目标位次 = " + position + "（在 rate 与 bufSize 之间）⇒ 本轮或下一轮轮到它。");
            }
            else
            {
                int cycles = (position / rate) + 1;
                SB.AppendLine("     目标位次 = " + position + " ⇒ 前面还有 " + position
                              + " 件可吸的排在它前面，按每轮 " + rate + " 件算，约 " + cycles + " 轮（≈ "
                              + (cycles * 15f / 60f).ToString("F1") + " 秒）后轮到它。");
            }
        }

        // ===================================================================
        // 判定链各段的"为什么"（每一条都真的调了对应的原版 API）
        // ===================================================================

        /// <summary>
        /// 逐条复刻 <c>ListerHaulables.ShouldBeHaulable</c>（<c>:194</c>）的四道门并返回第一条命中的原因。
        /// 返回 null = **四道门都不拦**，那它就该在待搬表里。
        /// </summary>
        private static string HaulableGateReason(Thing t, Map map)
        {
            if (t.IsForbidden(Faction.OfPlayer))
                return "被禁止（F3 解禁后会立刻入表）";

            if (!t.def.alwaysHaulable)
            {
                if (!t.def.EverHaulable)
                    return "def.EverHaulable = false（原版压根不搬这类东西）";
                if (map.designationManager.DesignationOn(t, DesignationDefOf.Haul) == null && !t.IsInAnyStorage())
                    return "没有「搬运」标记，而且它不在任何储存里（原版要求二者至少一个）";
            }

            if (t.IsInValidBestStorage())
                return "★ 原版认为它**已经在最优储存里**（CurrentHaulDestination 接受了它）"
                       + " ⇒ `ShouldBeHaulable` 直接剔除。看 ③ 的优先级对比与下面 ④ 的原因";

            IHaulSource asSource = t.ParentHolder as IHaulSource;
            if (asSource != null && !asSource.HaulSourceEnabled)
                return "它所在的容器关掉了出库（HaulSourceEnabled = false）";

            return null;
        }

        /// <summary>
        /// 逐条复刻 <c>WouldVanillaHaulIntoCore</c> 的判定并给出拒绝原因。
        /// </summary>
        private static string DestReason(Map map, Thing t, List<Building_StorageCore> cores)
        {
            StoragePriority curPrio = StoreUtility.CurrentStoragePriorityOf(t);

            IHaulDestination dest;
            bool found = StoreUtility.TryFindBestBetterNonSlotGroupStorageFor(
                t, null, map, curPrio, Faction.OfPlayer, out dest,
                acceptSamePriority: false, requiresDestReservation: false);

            if (!found)
            {
                StringBuilder tr = new StringBuilder();
                tr.Append("原版没找到更好的**非格子**容器（当前优先级 ").Append(curPrio)
                  .Append("；`acceptSamePriority: false` ⇒ 核心优先级必须**严格高于**它现在待的地方）");
                AppendDestinationTable(tr, map, t, cores, curPrio);
                return tr.ToString();
            }

            Building_StorageCore core = dest as Building_StorageCore;
            if (core == null)
                return "原版选的更好的容器不是我们的核心，而是 " + TypeName(dest) + "（核心输了）";

            // 格子腿：原版会让同级或更高优先级的格子储存赢
            IntVec3 cell;
            if (StoreUtility.TryFindBestBetterStoreCellFor(t, null, map, curPrio, Faction.OfPlayer, out cell, needAccurateResult: false))
            {
                SlotGroup cg = map.haulDestinationManager.SlotGroupAt(cell);
                StoragePriority cp = (cg == null) ? StoragePriority.Unstored : cg.Settings.Priority;
                if ((int)cp >= (int)core.GetStoreSettings().Priority)
                    return "存在同级或更高优先级的**格子储存** " + cell + "（优先级 " + cp
                           + " ≥ 核心 " + core.GetStoreSettings().Priority + "）⇒ 刻意让给格子";
            }

            if (!core.Powered)
                return "选中的核心 " + core.LabelCap + " 没通电（断电的核心原版也不会搬进去）";

            if (!core.Accepts(t))
                return "核心 " + core.LabelCap + " 的 Accepts() 返回 false（存储筛选不允许 / 容量满 / 已经装不下）"
                       + "；筛选允许=" + FilterAllows(core, t) + " 堆=" + core.innerContainer.Count + "/" + core.maxStacks
                       + " stackLimit=" + (t.def == null ? "?" : t.def.stackLimit.ToString())
                       + " 还能装=" + core.innerContainer.GetCountCanAccept(t)
                       + " 还能装(long)=" + ((CoreStorageContainer)core.innerContainer).CountCanAcceptSafe(t);

            return "（走到这里说明应该能进 —— 若仍不吸请把这整段发我）";
        }

        /// <summary>
        /// 把 <c>haulDestinationManager</c> 的目的地表整表复刻一遍。
        ///
        /// <para>原版 <c>StoreUtility.TryFindBestBetterNonSlotGroupStorageFor</c> 走的就是这张表、就是下面这几道门：
        /// 优先级降序 ⇒ 跳过 <c>ISlotGroupParent</c> / <c>!HaulDestinationEnabled</c> ⇒ 优先级闸 ⇒
        /// <c>Accepts</c> ⇒ 阵营 ⇒ 被禁。表里每一行的 <c>enabled / prio / accepts</c> 就是"为什么没选中核心"的直接答案。</para>
        /// </summary>
        private static void AppendDestinationTable(StringBuilder sb, Map map, Thing t,
            List<Building_StorageCore> cores, StoragePriority curPrio)
        {
            sb.AppendLine();
            sb.AppendLine("     —— 目的地表 dump（原版搜索顺序 = 优先级降序；当前件优先级 " + curPrio + "）——");
            sb.AppendLine("     —— 我方复刻（不调原版方法，只按同一串门重扫一遍）会选中：" + ReplicateBestContainer(map, t, curPrio) + " ——");

            List<IHaulDestination> list = map.haulDestinationManager.AllHaulDestinationsListForReading;
            for (int i = 0; i < list.Count; i++)
            {
                IHaulDestination d = list[i];
                string prio, en, acc;
                try
                {
                    StorageSettings ss = d.GetStoreSettings();
                    prio = (ss == null) ? "<null>" : ss.Priority.ToString();
                }
                catch (Exception e) { prio = "<ex:" + e.GetType().Name + ">"; }
                try { en = d.HaulDestinationEnabled.ToString(); } catch (Exception e) { en = "<ex:" + e.GetType().Name + ">"; }
                try { acc = d.Accepts(t).ToString(); } catch (Exception e) { acc = "<ex:" + e.GetType().Name + ">"; }

                sb.Append("       [").Append(i).Append("] ").Append(TypeName(d))
                  .Append(" prio=").Append(prio)
                  .Append(" enabled=").Append(en)
                  .Append(" accepts=").Append(acc);

                if (d is ISlotGroupParent) sb.Append("  ← 格子型：原版直接跳过");

                Thing th = d as Thing;
                if (th != null)
                {
                    sb.Append(" spawned=").Append(th.Spawned)
                      .Append(" 阵营=").Append(th.Faction == null ? "<null>" : th.Faction.Name)
                      .Append(" 被禁=").Append(th.IsForbidden(Faction.OfPlayer))
                      .Append(" 距离=").Append(th.PositionHeld.DistanceTo(t.PositionHeld).ToString("F1"));
                }
                else
                {
                    sb.Append("  ← 不是 Thing（没有阵营/被禁/距离这几道门）");
                }
                sb.AppendLine();
            }

            for (int i = 0; i < cores.Count; i++)
            {
                Building_StorageCore core = cores[i];
                sb.Append("       核心「").Append(core.LabelCap).Append("」")
                  .Append(" 在目的地表里=").Append(list.Contains(core))
                  .Append(" spawned=").Append(core.Spawned)
                  .Append(" 已毁=").Append(core.Destroyed)
                  .Append(" 通电=").Append(core.Powered)
                  .Append(" IsUsableNow=").Append(core.IsUsableNow)
                  .Append(" 入库开关=").Append(core.DevHaulDestinationSwitchOn)
                  .Append(" HaulDestinationEnabled=").Append(core.HaulDestinationEnabled)
                  .Append(" 优先级=").Append(core.GetStoreSettings().Priority)
                  .Append(" Accepts=").Append(core.Accepts(t))
                  .Append(" 筛选允许=").Append(FilterAllows(core, t))
                  .Append(" 堆=").Append(core.innerContainer.Count).Append("/").Append(core.maxStacks)
                  .Append(" stackLimit=").Append(t.def == null ? "?" : t.def.stackLimit.ToString())
                  .Append(" 还能装=").Append(core.innerContainer.GetCountCanAccept(t))
                  .Append(" 还能装(long)=").Append(((CoreStorageContainer)core.innerContainer).CountCanAcceptSafe(t))
                  .Append(" 被禁=").Append(core.IsForbidden(Faction.OfPlayer))
                  .Append(" 阵营=").Append(core.Faction == null ? "<null>" : core.Faction.Name)
                  .AppendLine();
            }
        }

        /// <summary>
        /// 我方复刻版「容器腿」：**不调** <c>StoreUtility.TryFindBestBetterNonSlotGroupStorageFor</c>，
        /// 只按它那串门自己扫一遍 <c>AllHaulDestinationsListInPriorityOrder</c>。
        ///
        /// <para>用途：分辨 <c>false</c> 的两种来源 ——</para>
        /// <list type="bullet">
        /// <item>复刻结果也是「无」⇒ 表里确实没有合格目的地（核心自身某道门没过 / 不在表里）；</item>
        /// <item>复刻结果选中了核心、原版却返回 false ⇒ 原版那个方法**被别的 mod 打了岔**（Prefix 短路 / Transpiler 改坏），
        /// 这时再怎么调原版结果都一样，我们得自己扫表（<c>WouldVanillaHaulIntoCore</c> 要加一条自扫兜底）。</item>
        /// </list>
        /// </summary>
        private static string ReplicateBestContainer(Map map, Thing t, StoragePriority curPrio)
        {
            List<IHaulDestination> list = map.haulDestinationManager.AllHaulDestinationsListInPriorityOrder;
            IntVec3 from = t.SpawnedOrAnyParentSpawned ? t.PositionHeld : IntVec3.Invalid;
            float bestDist = float.MaxValue;
            IHaulDestination best = null;

            for (int i = 0; i < list.Count; i++)
            {
                IHaulDestination d = list[i];
                if (d is ISlotGroupParent) continue;                 // 格子型：原版容器腿不认
                if (!d.HaulDestinationEnabled) continue;

                StoragePriority p;
                try
                {
                    StorageSettings ss = d.GetStoreSettings();
                    if (ss == null) continue;
                    p = ss.Priority;
                }
                catch (Exception) { continue; }

                // 列表是优先级降序 ⇒ 一旦掉到"不比当前件高"，后面不可能再有 → 原版在这里 break
                if ((int)p <= (int)curPrio) break;

                float dist = from.IsValid ? from.DistanceToSquared(d.Position) : 0f;
                if (dist > bestDist) continue;

                bool accepts;
                try { accepts = d.Accepts(t); } catch (Exception) { continue; }
                if (!accepts) continue;

                Thing th = d as Thing;
                if (th != null)
                {
                    if (th.Faction != Faction.OfPlayer) continue;
                    if (th.IsForbidden(Faction.OfPlayer)) continue;
                }

                best = d;
                bestDist = dist;
            }

            if (best == null) return "<无>";
            Thing bestThing = best as Thing;
            return TypeName(best) + (bestThing == null ? "" : "「" + bestThing.LabelShort + "」")
                   + " prio=" + best.GetStoreSettings().Priority + " @" + best.Position;
        }

        private static string Verdict(Thing t, bool inPool, CompAutoIngest.Reject rej,
            Building_StorageCore dest, List<Building_StorageCore> cores)
        {
            if (cores.Count == 0) return "本图没有核心，不可能被吸。";
            if (!inPool) return "★ 卡在**候选层**：不在原版待搬表里 ⇒ 自动收纳看不见它。这是最常见的一类。";
            if (rej != CompAutoIngest.Reject.None) return "★ 卡在**过滤层**：" + RejText(rej);
            if (dest == null) return "★ 卡在**目的地层**：原版不会把它搬进核心（见 ④ 的原因）。";
            return "①②③④ 全通过 ⇒ **它被检查时会通过**。是否真的吸到，取决于 ⑤ 队列深度"
                   + "（每轮只吸 rate 件）。要立刻验证就点「立刻收纳这一格（动手）」。";
        }

        // ===================================================================
        // 小工具
        // ===================================================================

        /// <summary>本图**所有**核心（含断电/关开关的）—— 诊断要比"可用核心"看得更宽。</summary>
        private static List<Building_StorageCore> FindAllCores(Map map)
        {
            var list = new List<Building_StorageCore>();
            List<IHaulSource> all = map.haulDestinationManager.AllHaulSourcesListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                Building_StorageCore c = all[i] as Building_StorageCore;
                if (c != null) list.Add(c);
            }
            return list;
        }

        private static bool FilterAllows(Building_StorageCore core, Thing t)
        {
            StorageSettings st = core.GetStoreSettings();
            return st == null || st.filter == null || st.filter.Allows(t);
        }

        private static bool ResearchDone(string defName)
        {
            ResearchProjectDef d = ResearchProjectDef.Named(defName);
            return d != null && d.IsFinished;
        }

        private static string RejText(CompAutoIngest.Reject r)
        {
            switch (r)
            {
                case CompAutoIngest.Reject.NullOrDestroyed: return "null 或已销毁";
                case CompAutoIngest.Reject.NotOnMap: return "不在图上（在别人背包/容器里）——不能对它 DeSpawn";
                case CompAutoIngest.Reject.InPrisonArea: return "在关押区（牢房或有囚犯的房间）——囚犯的饭不能被收";
                case CompAutoIngest.Reject.OnBillGiver: return "在工作台的材料区（= 工作台自己占的格）——收了会触发原版把料搬走的循环";
                case CompAutoIngest.Reject.OnBillGiverRim: return "在**还在等料的工作台旁边一圈**里、还没躺够宽限（300 tick）——先让位给原版/搬运工，躺够了自己会被收";
                case CompAutoIngest.Reject.Reserved: return "已被某个 pawn 预订";
                case CompAutoIngest.Reject.JobTarget: return "★ 它是某个 pawn **当前或排队中作业的目标**（原版/搬运 mod 正准备拿它）——吸走会让那个作业找不到东西、反复重取";
                case CompAutoIngest.Reject.RecentlyWithdrawn: return "刚被取出（300 tick 保护窗口内）";
                default: return r.ToString();
            }
        }

        private static string TypeName(object o)
        {
            return o == null ? "null" : o.GetType().Name;
        }

        private static string Brief(Thing t)
        {
            if (t == null) return "null";
            return t.def == null ? "(无 def)" : t.def.defName + " x" + t.stackCount + " [id=" + t.thingIDNumber + "]";
        }
    }
}
