using System;
using System.Collections.Generic;
using System.Reflection;
using DigitalStorage.AI;
using DigitalStorage.Effects;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// <b>代理建筑</b>（数字工人）：由建筑自己完成 挖掘/建造/清洁/种植(伐木) 之一，殖民者不跑腿。
    ///
    /// <para><b>路线</b>：不 spawn 任何 Pawn（"假 pawn 当资质载体"），而是照抄原版 driver 的
    /// "对目标干活"那一段，脱离 job 直接调用产出函数。三条路线为何这样选、以及为什么
    /// "完全不要 pawn"不成立（<c>Plant.PlantCollected(by,…)</c> 会解引用 <c>by</c>），
    /// 见 obsidian：<c>代码Wiki/rimworld/代理工人-脱离job直调产出.md</c>。</para>
    ///
    /// <para><b>并行</b>：普通代理建筑 <c>maxParallelPerWorkType = 1</c>（一次一件活）；
    /// 超凡代理 = 50 且管 4 个 workTypes ⇒ 总计最多 200 件同时进行。
    /// 因此整个 tick <b>只借还一次地图</b>（<see cref="DigitalWorkerScope"/>），而不是逐任务借还。</para>
    ///
    /// <para><b>四件必须记住的事</b>（都是踩过的）：
    /// ① 产出函数**没有** <c>Destroyed</c> 守卫 ⇒ 每次动手前后都要复查目标；
    /// ② 找活与干活都必须包在 <see cref="DigitalWorkerScope"/> 里 ——
    ///    <c>ReservationManager.CanReserve</c> 要求 <c>claimant.Spawned &amp;&amp; claimant.Map == map</c>
    ///    （<c>ReservationManager.cs:164-167</c>），而假 pawn 从不 spawn；
    /// ③ 借还 <c>mapIndexOrState</c> 必须 <c>try/finally</c>，漏一次 pawn 就永久挂在错误地图上；
    /// ④ 假 pawn 必须补 <c>PawnComponentsUtility.AddComponentsForSpawn</c>，否则任何读
    ///    <c>pawn.DrawPos</c> 的原版代码都会在 <c>PawnTweener.TweenedPosRoot</c> 里 NRE
    ///    （它直接解引用 <c>pawn.pather</c>）。</para>
    /// </summary>
    public class CompDigitalWorker : ThingComp
    {
        /// <summary>一件正在干的活 + 它自己的表现件（手 / 黄色读条）。</summary>
        private class ActiveWork
        {
            public DigitalTask task;
            public Map map;
            public Mote_DS_WorkHand hand;
            public Effecter bar;
            public Effecter hitFx;
            public int lastStrikes;
        }

        /// <summary>资质载体。**不进存档**（可重建的派生对象），读档后按需重建。</summary>
        private Pawn worker;

        private readonly List<ActiveWork> works = new List<ActiveWork>();
        private int nextScanTick;
        private bool enabled = true;

        /// <summary>候选集遍历上限（防某个 lister 把一帧吃光）。**每次 giver 调用**的上限。</summary>
        private const int MaxIterate = 4000;

        /// <summary>
        /// <b>一次扫描（跨所有 add、所有 giver）的总枚举额度</b>。
        ///
        /// <para>⚠️ 2026 实测踩坑：原先只有 <see cref="MaxIterate"/>（每次 add 4000），
        /// 而一次扫描最多 <see cref="AddsPerScan"/> 个 add ⇒ 最坏 <b>60 × 4000 = 24 万次枚举</b>，
        /// 实测 33ms/帧（240000 × ~137ns），是当时卡顿的第一大头（`DS-scan` 占 CompTick 的 65%）。
        /// 用户实测场景：4 个超凡代理 + 全图 2.5 万个标记，每个候选还要过一遍
        /// <c>HasJobOnThing</c>，乘积爆炸。</para>
        ///
        /// <para>额度取 6000 ≈ 0.8ms/次扫描：稳态下第一个可认领的候选通常在枚举前几百个内
        /// （已认领的会被快速跳过、完成的会从候选集里消失），所以 60 个 add 照样能填满。</para>
        /// </summary>
        private const int MaxEnumerationsPerScan = 6000;

        private int scanBudget;

        /// <summary>扫描序号：用来判断候选集缓存是不是本次扫描已物化过。</summary>
        private int scanSerial;

        /// <summary>
        /// <b>候选集缓存（一次扫描内每个 giver 只物化一次）</b>。
        ///
        /// <para>⚠️ 2026 实测踩坑：<c>CandidateSet</c> 原本是**每次 add 都调**的，
        /// 而清洁适配器的候选集是"扫全图 <c>listerThings.AllThings</c> 过滤出污物"
        /// （<c>ThingRequestGroup.Filth</c> 在 <c>ThingListGroupHelper</c> 里没有分支，拿不到列表，
        /// 只能这么写）。一次扫描最多 <see cref="AddsPerScan"/>=60 个 add ⇒
        /// **60 次全图扫描**（用户场景：4 个超凡代理 + 全图 2 万岩屑 + 几十万 item）
        /// ⇒ 每次扫描 15ms+，还每帧分配几十 MB（实测 分配 111MB/窗口）。
        /// 物化一次之后，枚举开销降到 1/60。</para>
        ///
        /// <para>用 <c>Clear()</c> 复用 List 容量而不是丢弃重建，避免 GC 抖动。
        /// 键是 <c>WorkGiver_Scanner</c>（<c>def.Worker</c> 单例，身份稳定）。</para>
        /// </summary>
        private readonly Dictionary<WorkGiver, List<Thing>> candidateCache =
            new Dictionary<WorkGiver, List<Thing>>();

        private readonly Dictionary<WorkGiver, int> candidateSerial = new Dictionary<WorkGiver, int>();

        /// <summary>
        /// <b>格子型候选缓存</b>（播种 / 自动收割那类 scanCells 型 giver）。
        ///
        /// <para>⚠️ 物化时必须**一次性把格子全取出来**，不能边枚举边问原版：原版
        /// <c>WorkGiver_Grower</c> 靠"枚举时顺手写 <c>wantedPlantDef</c> 静态字段"给
        /// <c>JobOnCell</c> 传值，而取活发生在枚举之后 ⇒ 只能由我们自己把它清成 null
        /// 让原版按格子重算（见 <c>DigitalPlantWork.ResetWantedPlantDef</c>）。</para>
        ///
        /// <para>⚠️ <b>缓存跨扫描存活</b>（<see cref="CellCacheRefreshTicks"/>），不像 Thing 候选集
        /// 那样一次扫描就失效：格子的枚举贵在<b>每个 settable 一次 <c>pawn.CanReach</c></b>
        /// （<c>WorkGiver_Grower.PotentialWorkCellsGlobal</c>），而代理建筑**从不移动**、
        /// 种植区也不会每 15 tick 变一次。原版殖民者是一次找活枚举一次（还会在拿到活的瞬间
        /// 早退），我们要按 AddsPerScan 反复取用 ⇒ 不缓存就是每秒几十次全图寻路。
        /// 代价：新建的种植区最多 <see cref="CellCacheRefreshTicks"/> tick（4 秒）后才被发现，
        /// 而那些已失效的格子由 <c>MakeCellTask</c>/<c>StillValid</c> 廉价跳过。</para>
        /// </summary>
        private readonly Dictionary<WorkGiver, List<IntVec3>> cellCache =
            new Dictionary<WorkGiver, List<IntVec3>>();

        private readonly Dictionary<WorkGiver, int> cellCacheTick = new Dictionary<WorkGiver, int>();

        private const int CellCacheRefreshTicks = 240;

        /// <summary>
        /// 一个格子型 giver 一次扫描最多物化几格。
        ///
        /// <para>防"一个 200×200 的种植区把一次扫描吃光"：格子型候选集的枚举本身
        /// （<c>PotentialWorkCellsGlobal</c>）是**没有上限**的，而我们要把它拷进 List。
        /// 4000 格 ≈ 一次 32KB 的拷贝，与挖掘的 <see cref="MaxIterate"/> 同量级。
        /// 超出的尾部这一趟看不到，但下一趟从头上再走时，已认领的格子会被廉价跳过。</para>
        /// </summary>
        private const int MaxCellCandidates = 4000;

        /// <summary>一次扫描最多新增几件活（实测不卡，给大一点让并行舰队快速填满）。</summary>
        private const int AddsPerScan = 60;

        /// <summary>没填满时用更短的扫描间隔，避免"并行数爬升很慢"。</summary>
        private const int FillScanIntervalTicks = 15;

        public CompProperties_DigitalWorker Props
        {
            get { return (CompProperties_DigitalWorker)props; }
        }

        /// <summary>同时进行几件活（UI/调试用）。</summary>
        public int ActiveCount
        {
            get { return works.Count; }
        }

        public DigitalTask CurrentTask
        {
            get { return works.Count > 0 ? works[0].task : null; }
        }

        public bool Enabled
        {
            get { return enabled; }
            set
            {
                enabled = value;
                if (!enabled) ReleaseAll();
            }
        }

        /// <summary>有电才能干活 —— 沿用核心"取出的唯一门就是电"的既有口径。</summary>
        public bool Powered
        {
            get
            {
                CompPowerTrader p = parent.GetComp<CompPowerTrader>();
                return p == null || p.PowerOn;
            }
        }

        public bool CanWork
        {
            get { return parent.Spawned && !parent.Destroyed && Powered && enabled; }
        }

        public Pawn Worker
        {
            get
            {
                EnsureWorker();
                return worker;
            }
        }

        /// <summary>只在已经建好时返回，**不会触发创建** —— 给热路径（如 <c>CanReserve</c> 补丁）用。</summary>
        public Pawn WorkerIfCreated
        {
            get { return worker; }
        }

        // ===================================================================
        // 主循环
        // ===================================================================

        public override void CompTick()
        {
            // 断电 / 被拆 / 关掉 ⇒ 立刻全放手（不占着目标）
            if (!CanWork)
            {
                ReleaseAll();
                return;
            }

            Map map = parent.Map;
            if (map == null) return;

            Pawn w = Worker;
            if (w == null) return;

            // 分帧推进：把"要 O(全图) 才能算出来的候选集"每 tick 推一小片
            // （适配器的默认实现是空操作，目前只有清洁会真的动）。
            // 位置刻意放在这里 —— 与扫描频率无关，尖峰才能真正变成常量。
            SliceCandidates(map, w);

            long heap0 = Performance.DevDrawProfiler.HeapStamp();
            DigitalWorkerScope.Enter(w, map, parent.PositionHeld);
            // 本次 tick 期间的掉落直塞目标（没有可用核心时 Active=false，整条链路零开销）
            Core.DigitalDropRedirect.Begin(map, parent.PositionHeld);
            try
            {
                // 1) 丢掉失效的（目标没了 / 设计取消 / 被别人订走）
                long p = Performance.DevDrawProfiler.Stamp();
                for (int i = works.Count - 1; i >= 0; i--)
                {
                    if (!works[i].task.StillValid(w, map)) ReleaseWorkAt(i);
                }
                Performance.DevDrawProfiler.Mark("DS-valid", p);

                // 2) 补到并行上限（按 workTypes 各自的配额；限流见 AddsPerScan）
                p = Performance.DevDrawProfiler.Stamp();
                int now = Find.TickManager.TicksGame;
                if (now >= nextScanTick)
                {
                    Performance.DevDrawProfiler.Bump("扫描", 1);
                    int interval = Math.Max(1, Props.scanIntervalTicks);
                    int totalCap = TotalParallelCap();
                    if (totalCap > 0 && works.Count < totalCap)
                    {
                        interval = Math.Min(interval, FillScanIntervalTicks);   // 没填满 ⇒ 快扫
                    }
                    nextScanTick = now + interval;
                    scanBudget = MaxEnumerationsPerScan;   // 本次扫描的总枚举额度（跨 add / 跨 giver）
                    scanSerial++;                          // 候选集缓存按扫描序号失效
                    for (int added = 0; added < AddsPerScan; added++)
                    {
                        if (scanBudget <= 0) break;
                        if (!TryAddOneWork(map, w)) break;
                    }
                    Performance.DevDrawProfiler.Bump("枚举", MaxEnumerationsPerScan - scanBudget);
                }
                Performance.DevDrawProfiler.Mark("DS-scan", p);

                // 3) 干活（每件活各自累积）
                p = Performance.DevDrawProfiler.Stamp();
                for (int i = 0; i < works.Count; i++)
                {
                    try
                    {
                        works[i].task.Work(w, map, Props.workSpeedMult);
                    }
                    catch (Exception e)
                    {
                        Log.Error("[DigitalStorage] 代理建筑干活出错（丢这一件）：" + parent + " → " + e);
                        ReleaseWorkAt(i);
                        i--;
                    }
                }
                Performance.DevDrawProfiler.Mark("DS-work", p);

                // 4) 收掉干完的
                p = Performance.DevDrawProfiler.Stamp();
                for (int i = works.Count - 1; i >= 0; i--)
                {
                    if (works[i].task.Finished)
                    {
                        Performance.DevDrawProfiler.Bump("完成", 1);
                        ReleaseWorkAt(i);
                    }
                }
                Performance.DevDrawProfiler.Mark("DS-finish", p);
            }
            finally
            {
                Core.DigitalDropRedirect.End();
                DigitalWorkerScope.Exit(w);
            }

            long pv = Performance.DevDrawProfiler.Stamp();
            UpdateVisuals(map);
            Performance.DevDrawProfiler.Mark("DS-visual", pv);
            Performance.DevDrawProfiler.MarkAlloc("DS-allocMB", heap0);
        }

        private List<WorkTypeDef> effectiveWorkTypes;

        /// <summary>
        /// <b>实际要扫的工作类型</b> = XML 配的 <see cref="CompProperties_DigitalWorker.workTypes"/>
        /// ∪ 各适配器的跟随集（<see cref="DigitalTaskRegistry.ExpandFollowed"/>）。
        ///
        /// <para>为什么不能只信 XML：拆分工作类型的 mod（Complex Jobs 的 <c>FSF*</c> 那一族）
        /// 在**载入时**改 <c>WorkGiverDef.workType</c>，而建筑 XML 是静态的 ——
        /// 表现就是"建造代理会建、不拆"甚至"什么活都不干"，且<b>一条日志都没有</b>
        /// （wiki `专属Bug记录` 里登记为静默失效；2026-10-20 本次修掉）。</para>
        /// </summary>
        internal List<WorkTypeDef> EffectiveWorkTypes
        {
            get
            {
                if (effectiveWorkTypes == null)
                {
                    effectiveWorkTypes = DigitalTaskRegistry.ExpandFollowed(Props.workTypes);
                }
                return effectiveWorkTypes;
            }
        }

        /// <summary>再找一件活（受"每类并行配额"限制）。找到并认领返回 true。</summary>
        private bool TryAddOneWork(Map map, Pawn w)
        {
            List<WorkTypeDef> types = EffectiveWorkTypes;
            if (types == null) return false;

            if (Props.maxParallelTotal > 0 && works.Count >= Props.maxParallelTotal) return false;

            int cap = Math.Max(1, Props.maxParallelPerWorkType);
            for (int i = 0; i < types.Count; i++)
            {
                WorkTypeDef wt = types[i];
                if (wt == null) continue;
                if (scanBudget <= 0) return false;          // 枚举额度用完 ⇒ 本次扫描到此为止
                if (CountWorksOf(wt) >= cap) continue;
                if (TryScanWorkType(map, w, wt)) return true;
            }
            return false;
        }

        private int CountWorksOf(WorkTypeDef wt)
        {
            int n = 0;
            for (int i = 0; i < works.Count; i++)
            {
                if (works[i].task.workType == wt) n++;
            }
            return n;
        }

        /// <summary>把本建筑关心的每个 giver 的候选集刷新推进一步（分帧；多数适配器是空操作）。</summary>
        private void SliceCandidates(Map map, Pawn w)
        {
            List<WorkTypeDef> types = EffectiveWorkTypes;
            if (types == null) return;
            for (int i = 0; i < types.Count; i++)
            {
                WorkTypeDef wt = types[i];
                if (wt == null) continue;
                List<WorkGiver> givers = DigitalTaskRegistry.FindGivers(wt);
                if (givers == null) continue;
                for (int g = 0; g < givers.Count; g++)
                {
                    WorkGiver_Scanner scanner = givers[g] as WorkGiver_Scanner;
                    if (scanner == null) continue;
                    DigitalTaskAdapter adapter = DigitalTaskRegistry.AdapterFor(givers[g]);
                    if (adapter == null) continue;
                    adapter.SliceTick(map, w, scanner);
                }
            }
        }

        /// <summary>总并行上限（含 maxParallelTotal 与"每类配额 × 类别数"取小）。</summary>
        private int TotalParallelCap()
        {
            List<WorkTypeDef> types = Props.workTypes;
            int cap = Math.Max(1, Props.maxParallelPerWorkType) * (types != null && types.Count > 0 ? types.Count : 1);
            if (Props.maxParallelTotal > 0 && Props.maxParallelTotal < cap) cap = Props.maxParallelTotal;
            return cap;
        }

        private bool TryScanWorkType(Map map, Pawn w, WorkTypeDef workType)
        {
            // ⚠️ 一个工作类型可能有**多个**有适配器的 WorkGiver（Construction = 建造 + 拆除 + 维修…），
            // 必须全试 —— 早先只取第一个，表现就是"建造代理永远不拆建筑"。
            List<WorkGiver> givers = DigitalTaskRegistry.FindGivers(workType);
            if (givers == null) return false;

            for (int g = 0; g < givers.Count; g++)
            {
                if (scanBudget <= 0) return false;          // 枚举额度用完 ⇒ 换下一个工作类型也没意义
                if (TryScanGiver(map, w, workType, givers[g])) return true;
            }
            return false;
        }

        private bool TryScanGiver(Map map, Pawn w, WorkTypeDef workType, WorkGiver giver)
        {
            WorkGiver_Scanner scanner = giver as WorkGiver_Scanner;
            if (scanner == null) return false;

            DigitalTaskAdapter adapter = DigitalTaskRegistry.AdapterFor(giver);
            if (adapter == null) return false;

            if (!DigitalTaskRegistry.PawnCanUse(giver, w, adapter)) return false;

            // scanCells 型 giver（播种/自动收割）：候选是格子，闸门在 JobOnCell/HasJobOnCell 里
            if (adapter.CellBased) return TryScanGiverCells(map, w, workType, scanner, adapter);

            List<Thing> set = GetCandidates(map, w, scanner, adapter);
            if (set == null || set.Count == 0) return false;

            int seen = 0;
            foreach (Thing t in set)
            {
                if (--scanBudget < 0) break;      // 本次扫描的枚举额度（跨 add / 跨 giver）
                if (++seen > MaxIterate) break;
                if (t == null || t.Destroyed || !t.Spawned) continue;

                // 不抢别人（含原版殖民者）已认领的目标；
                // ⚠️ 也必须跳过**本建筑自己**已认领的 —— 否则同一次扫描会把同一个目标
                // 反复认领成多件活（并行 200 时 = 200 件活全砸在同一块矿上，
                // 表现成"只有一个目标"，而且瞬间挖穿）
                if (t.IsForbidden(w)) continue;
                if (DigitalWorkerClaims.OwnerOf(map, t) != null) continue;
                if (!adapter.CanTarget(w, t)) continue;

                if (adapter.TrustWorkGiver)
                {
                    bool hasJob;
                    try
                    {
                        Performance.DevDrawProfiler.Bump("试岗", 1);   // HasJobOnThing 调用次数
                        hasJob = scanner.HasJobOnThing(w, t, false);
                    }
                    catch (Exception e)
                    {
                        Log.ErrorOnce("[DigitalStorage] 问 WorkGiver 时出错：" + giver.def.defName + " → " + e,
                            giver.def.shortHash * 31 + 9923);
                        hasJob = false;
                    }
                    if (!hasJob) continue;
                }

                DigitalTask candidate = adapter.MakeTask(t, this);
                candidate.workType = workType;
                if (!candidate.StillValid(w, map)) continue;

                candidate.Claim(map, this);
                works.Add(new ActiveWork { task = candidate, map = map });
                return true;
            }
            return false;
        }

        /// <summary>
        /// <b>格子型找活</b>（原版 scanCells 型 <c>WorkGiver</c>）。
        ///
        /// <para>与 Thing 那条路的差别只有三处：候选是格子、取活由适配器问原版
        /// （<c>JobOnCell</c>/<c>HasJobOnCell</c>）、认领走 <c>DigitalTask.Claim</c>
        /// —— 播种的苗此刻还不存在，只能占格子（否则两个代理会把苗种进同一格）。</para>
        /// </summary>
        private bool TryScanGiverCells(Map map, Pawn w, WorkTypeDef workType, WorkGiver_Scanner scanner, DigitalTaskAdapter adapter)
        {
            List<IntVec3> cells = GetCandidateCells(map, w, scanner, adapter);
            if (cells == null || cells.Count == 0) return false;

            int seen = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                if (--scanBudget < 0) break;      // 本次扫描的总枚举额度
                if (++seen > MaxIterate) break;

                IntVec3 c = cells[i];
                if (!c.IsValid || !c.InBounds(map)) continue;
                if (DigitalWorkerClaims.CellOwnerOf(map, c) != null) continue;

                DigitalTask candidate = adapter.MakeCellTask(c, map, w, scanner, this);
                if (candidate == null) continue;
                candidate.workType = workType;
                if (!candidate.StillValid(w, map)) continue;

                candidate.Claim(map, this);
                works.Add(new ActiveWork { task = candidate, map = map });
                return true;
            }
            return false;
        }

        /// <summary>格子型候选集（跨扫描缓存，见 <see cref="cellCache"/> 的说明）。</summary>
        private List<IntVec3> GetCandidateCells(Map map, Pawn w, WorkGiver_Scanner scanner, DigitalTaskAdapter adapter)
        {
            List<IntVec3> list;
            if (!cellCache.TryGetValue(scanner, out list))
            {
                list = new List<IntVec3>();
                cellCache[scanner] = list;
                cellCacheTick[scanner] = -1;
            }

            int now = Find.TickManager.TicksGame;
            int last;
            if (cellCacheTick.TryGetValue(scanner, out last) && last >= 0 && now - last < CellCacheRefreshTicks)
            {
                return list;
            }

            list.Clear();
            long t0 = Performance.DevDrawProfiler.Stamp();
            IEnumerable<IntVec3> set = adapter.CandidateCells(map, w, scanner);
            if (set != null)
            {
                foreach (IntVec3 c in set)
                {
                    if (list.Count >= MaxCellCandidates) break;
                    list.Add(c);
                }
            }
            Performance.DevDrawProfiler.Mark("ScanSet", t0);
            Performance.DevDrawProfiler.Bump("物化格", 1);
            cellCacheTick[scanner] = now;
            return list;
        }

        /// <summary>
        /// 取候选集（**一次扫描内每个 giver 只物化一次**，见 <see cref="candidateCache"/>）。
        /// 物化拷贝到这里而不是直接枚举 <c>CandidateSet</c>，是因为它会**每次调用都全图扫一遍**
        /// （清洁适配器）；而一次扫描要调它最多 60 次。
        /// </summary>
        private List<Thing> GetCandidates(Map map, Pawn w, WorkGiver_Scanner scanner, DigitalTaskAdapter adapter)
        {
            List<Thing> list;
            if (!candidateCache.TryGetValue(scanner, out list))
            {
                list = new List<Thing>();
                candidateCache[scanner] = list;
                candidateSerial[scanner] = int.MinValue;
            }
            if (candidateSerial[scanner] == scanSerial) return list;

            list.Clear();
            long t0 = Performance.DevDrawProfiler.Stamp();
            IEnumerable<Thing> set = adapter.CandidateSet(map, w, scanner);
            if (set != null)
            {
                foreach (Thing t in set)
                {
                    if (t != null) list.Add(t);
                }
            }
            Performance.DevDrawProfiler.Mark("ScanSet", t0);
            Performance.DevDrawProfiler.Bump("物化", 1);
            candidateSerial[scanner] = scanSerial;
            return list;
        }

        /// <summary>放手一件活（清它那份认领 + 收掉它的表现件）。</summary>
        private void ReleaseWorkAt(int i)
        {
            ActiveWork aw = works[i];
            works.RemoveAt(i);
            aw.task.Release(aw.map, this);   // Thing 型 = 放目标物；格子型 = 放格子（任务自己决定）
            aw.task.Cleanup();
            CleanupVisual(aw);
        }

        /// <summary>全部放手（断电/拆除/关闭/出异常）。</summary>
        public void ReleaseAll()
        {
            for (int i = works.Count - 1; i >= 0; i--) ReleaseWorkAt(i);
            DigitalWorkerClaims.ReleaseAll(this);   // 兜底：万一有漏网的条目
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            ReleaseAll();
            // 格子候选缓存带 4 秒 TTL（见 cellCache），而"被搬走/换图"正好是它唯一会失效的场合
            // （建筑换了图，缓存的还是旧图的格子）⇒ 这里顺手清掉
            cellCache.Clear();
            cellCacheTick.Clear();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref enabled, "enabled", true);
            // 刻意不 Scribe worker / works：工人与进行中的活都是可重建的派生状态，
            // 读档后重新找活即可（比存一份可能对不上的认领表更安全）。
        }

        /// <summary>
        /// 检查面板状态行（走 keyed，见 <c>Languages/*/Keyed</c> 的 <c>DS_DW_*</c> 与 <c>DS_Task_*</c>）。
        /// </summary>
        public override string CompInspectStringExtra()
        {
            if (!parent.Spawned) return null;
            if (!Powered) return "DS_DW_InspectOff".Translate().ToString();
            if (!enabled) return "DS_DW_InspectDisabled".Translate().ToString();

            int cap = TotalParallelCap();

            if (works.Count == 0)
            {
                if (Find.TickManager.TicksGame < nextScanTick) return "DS_DW_InspectIdle".Translate().ToString();
                return "DS_DW_InspectIdleNoTarget".Translate().ToString();
            }

            DigitalTask first = works[0].task;
            return "DS_DW_InspectWork".Translate(first.Label, first.TargetLabel,
                Props.workSpeedMult.ToString("0.0"), Props.skillLevel, works.Count, cap).ToString();
        }

        // ===================================================================
        // 表现：目标上的那只手 + 目标底下的黄色读条（都只画前 N 件，见 maxVisualTasks）
        // ===================================================================

        private void UpdateVisuals(Map map)
        {
            int cap = Math.Max(0, Props.maxVisualTasks);
            for (int i = 0; i < works.Count; i++)
            {
                ActiveWork aw = works[i];
                if (i >= cap)
                {
                    CleanupVisual(aw);
                    continue;
                }

                // 目标可能是 Thing（挖矿/建造/伐木/收割…），也可能是**格子**（播种：苗还不存在）。
                // 表现层只关心"画在哪一格"，所以这里把两种折成 (cell, 落点, TargetInfo)。
                Thing t = aw.task.target;
                bool hasThing = t != null && !t.Destroyed && t.Spawned;
                IntVec3 cell = hasThing ? t.Position : aw.task.TargetCell;
                if (!cell.IsValid || !cell.InBounds(map))
                {
                    CleanupVisual(aw);
                    continue;
                }
                Vector3 drawPos = hasThing ? t.DrawPos : cell.ToVector3Shifted();
                TargetInfo targetInfo = hasThing ? new TargetInfo(t) : new TargetInfo(cell, map);

                // ---- 手 ----
                if (aw.hand == null || aw.hand.Destroyed || !aw.hand.Spawned)
                {
                    ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(aw.task.HandMoteDefName);
                    if (def != null)
                    {
                        Mote_DS_WorkHand m = ThingMaker.MakeThing(def) as Mote_DS_WorkHand;
                        if (m != null)
                        {
                            GenSpawn.Spawn(m, cell, map);
                            aw.hand = m;
                        }
                    }
                }
                if (aw.hand != null && !aw.hand.Destroyed)
                {
                    Vector3 pos = drawPos;
                    pos.y = 0f;        // y 由 Mote.DrawMote 按 altitudeLayer 每帧重设
                    pos.z += 0.15f;    // 略微朝镜头，压在目标正面
                    aw.hand.exactPosition = pos;
                    aw.hand.Maintain();   // 不再 Maintain 时它 1 秒后自愈消失
                    if (aw.task.StrikeCount != aw.lastStrikes)
                    {
                        aw.lastStrikes = aw.task.StrikeCount;
                        aw.hand.Strike();

                        // 命中特效也走同一套"只画前 N 件"的上限 —— 否则并行 200 时
                        // 会出现"矿上有特效、但那只手不在"的错位。
                        // Trigger 的两个目标都用目标物本身：sprayer 不会去读假 pawn 的 DrawPos。
                        if (aw.task.HitEffecterDef != null)
                        {
                            if (aw.hitFx == null) aw.hitFx = aw.task.HitEffecterDef.Spawn();
                            aw.hitFx.Trigger(targetInfo, targetInfo);
                        }
                    }
                }

                // ---- 黄色读条（原版 EffecterDefOf.ProgressBar + MoteProgressBar）----
                float p = aw.task.Progress01;
                if (p < 0f)
                {
                    if (aw.bar != null)
                    {
                        aw.bar.Cleanup();
                        aw.bar = null;
                    }
                    continue;
                }
                if (aw.bar == null) aw.bar = EffecterDefOf.ProgressBar.Spawn();
                aw.bar.EffectTick(targetInfo, TargetInfo.Invalid);

                MoteProgressBar mote = (aw.bar.children.Count > 0)
                    ? (aw.bar.children[0] as SubEffecter_ProgressBar)?.mote
                    : null;
                if (mote != null)
                {
                    mote.progress = Mathf.Clamp01(p);
                    mote.offsetZ = -0.5f;     // 原版 WithProgressBar 的默认位置（贴在目标"底下"）
                    mote.alwaysShow = true;   // 代理可能在远离镜头处干活，别只在最近缩放才画
                }
            }
        }

        private void CleanupVisual(ActiveWork aw)
        {
            if (aw.hand != null && !aw.hand.Destroyed) aw.hand.Destroy();
            aw.hand = null;
            if (aw.bar != null)
            {
                aw.bar.Cleanup();
                aw.bar = null;
            }
            if (aw.hitFx != null)
            {
                aw.hitFx.Cleanup();
                aw.hitFx = null;
            }
            aw.lastStrikes = 0;
        }

        // ===================================================================
        // 资质载体（假 pawn）
        // ===================================================================

        /// <summary>
        /// 造资质载体。<b>生成逻辑已抽到 <see cref="DigitalWorkerFactory"/></b>（制作代理的
        /// <c>CompBillAutomation</c> 要用同一套 —— 组件补齐/不进注册表/清特质/固定资质
        /// 四条坑，两处各写一份必然漂移）。
        /// </summary>
        private void EnsureWorker()
        {
            if (worker != null && !worker.Destroyed) return;
            string nick = (Props.workTypes != null && Props.workTypes.Count > 0 ? Props.workTypes[0].defName : "?")
                + Props.skillLevel;
            worker = DigitalWorkerFactory.Create(Props.skillLevel, "数字工人", nick);
        }
    }

    /// <summary>
    /// <b>假 pawn 的"借地图"作用域</b>：临时把 <c>mapIndexOrState</c> 与 <c>Position</c> 设成真的，
    /// 让读 <c>pawn.Map</c> / 要求 <c>claimant.Spawned</c> 的原版代码放行。
    ///
    /// <para>先例：<c>god hand GodAssistantController.cs:288-318</c>（它用在制作产物上）。
    /// 我们比它多一个理由：<b>找活阶段也要借</b> —— <c>WorkGiver.HasJobOnThing</c> 里就有
    /// <c>CanReserve</c>，而它要求 <c>claimant.Spawned &amp;&amp; claimant.Map == map</c>。
    /// 但作用域要**尽量窄**，且必须 <c>try/finally</c>；并行 200 时**整个 tick 只借还一次**
    /// （逐任务借还就是每 tick 几百次反射写）。</para>
    ///
    /// <para>顺序很关键：进入时<b>先设 Position 再翻 mapIndexOrState</b>（<c>Position</c> 的 setter
    /// 在 <c>Spawned</c> 时会去动 region/lister）；退出时<b>先翻回 -1 再清 Position</b>。</para>
    /// </summary>
    internal static class DigitalWorkerScope
    {
        private static readonly FieldInfo MapIndexField = AccessTools.Field(typeof(Thing), "mapIndexOrState");

        public static void Enter(Pawn pawn, Map map, IntVec3 cell)
        {
            if (pawn == null || map == null || MapIndexField == null) return;
            // ★ 每次进作用域都把假 pawn 的 AI 关掉（工厂里已经设过一次，这里是双保险：
            //   原版 `Pawn_HealthTracker.cs:335` 会在健康状态变化时把 mindState.Active 翻回 true）。
            //   我们只是借它当地图上的"手"；一旦它看起来 Spawned（下面直接把 mapIndexOrState 设成
            //   map.Index）而 AI 还开着，原版就可能给它派活/让它寻路，而我们在本 tick 末尾就把它"退图"
            //   ⇒ 留下一条指向它的 PathRequest ⇒ PathFinder 每 tick 报
            //   "Tried to FindPath for pawn which is spawned in another map"（用户实测日志：
            //   pawn=数字工人 pawn.Map=null）。注意这条只是防"原版主动驱动它"，
            //   真正踩过的触发点是 `Blueprint.TryReplaceWithSolidThing` 里的
            //   `workerPawn.jobs.EndCurrentJob(...)`（见 DigitalTask_Construct.Work 阶段 0）。
            if (pawn.mindState != null) pawn.mindState.Active = false;
            pawn.Position = cell.IsValid ? cell : map.Center;
            MapIndexField.SetValue(pawn, (sbyte)map.Index);
        }

        public static void Exit(Pawn pawn)
        {
            if (pawn == null || MapIndexField == null) return;
            MapIndexField.SetValue(pawn, (sbyte)(-1));
            pawn.Position = IntVec3.Invalid;
        }
    }

    /// <summary>
    /// <b>图级认领表</b>：替代做不到的真预约。
    ///
    /// <para><c>ReservationManager.Reserve(pawn, job: null, …)</c> 会直接
    /// <c>Log.Warning + return false</c> ⇒ 绕开 job 就无法正规预约；用假 Job 预约则会把假 Job
    /// 写进存档、读档找不回来 = 永久占着某块矿。所以只能"自有认领表 + 只读 <c>CanReserve</c> 过滤"。</para>
    ///
    /// <para>认领表**不进存档**：读档后所有代理重新找活即可（比存一份可能对不上的表更安全）。
    /// 一个建筑可以同时认领多件活（超凡代理并行 200）⇒ 放行 <c>owner == me</c>。</para>
    ///
    /// <para><b>两张表</b>：目标物表（<c>Thing</c>）× 格子表（<c>IntVec3</c>）。
    /// 格子表是给"苗还不存在"的活（播种）用的 —— 那种活没有 Thing 可以占，
    /// 不占格子就会两个代理把苗种进同一格。</para>
    /// </summary>
    internal static class DigitalWorkerClaims
    {
        private static readonly Dictionary<Map, Dictionary<Thing, CompDigitalWorker>> claims =
            new Dictionary<Map, Dictionary<Thing, CompDigitalWorker>>();

        private static readonly Dictionary<Map, Dictionary<IntVec3, CompDigitalWorker>> cellClaims =
            new Dictionary<Map, Dictionary<IntVec3, CompDigitalWorker>>();

        private static Dictionary<Thing, CompDigitalWorker> For(Map map)
        {
            Dictionary<Thing, CompDigitalWorker> d;
            if (!claims.TryGetValue(map, out d))
            {
                d = new Dictionary<Thing, CompDigitalWorker>();
                claims[map] = d;
            }
            return d;
        }

        private static Dictionary<IntVec3, CompDigitalWorker> ForCells(Map map)
        {
            Dictionary<IntVec3, CompDigitalWorker> d;
            if (!cellClaims.TryGetValue(map, out d))
            {
                d = new Dictionary<IntVec3, CompDigitalWorker>();
                cellClaims[map] = d;
            }
            return d;
        }

        /// <summary>认领表里有没有任何条目 —— <c>CanReserve</c> 补丁的廉价早退用（热路径）。</summary>
        public static bool AnyClaims
        {
            get { return claims.Count > 0 || cellClaims.Count > 0; }
        }

        /// <summary>这个目标被哪个代理建筑认领了（没有/已失效则返回 null，并顺手清理失效项）。</summary>
        public static CompDigitalWorker OwnerOf(Map map, Thing t)
        {
            if (map == null || t == null) return null;
            Dictionary<Thing, CompDigitalWorker> d;
            if (!claims.TryGetValue(map, out d)) return null;
            CompDigitalWorker owner;
            if (!d.TryGetValue(t, out owner)) return null;
            if (owner == null || owner.parent == null || !owner.parent.Spawned)
            {
                d.Remove(t);
                return null;
            }
            return owner;
        }

        public static bool IsClaimedByOther(Map map, Thing t, CompDigitalWorker me)
        {
            CompDigitalWorker owner = OwnerOf(map, t);
            return owner != null && owner != me;
        }

        /// <summary>被**任何**代理认领了（包括自己）—— 找活时必须跳过这种目标。</summary>
        public static bool IsClaimedByAnyone(Map map, Thing t)
        {
            return OwnerOf(map, t) != null;
        }

        public static void TryClaim(Map map, Thing t, CompDigitalWorker me)
        {
            if (map == null || t == null || me == null) return;
            For(map)[t] = me;
        }

        /// <summary>只放掉**这一件**（并行时不能把别的活一起放了）。</summary>
        public static void Release(Map map, Thing t, CompDigitalWorker me)
        {
            if (map == null || t == null) return;
            Dictionary<Thing, CompDigitalWorker> d;
            if (!claims.TryGetValue(map, out d)) return;
            CompDigitalWorker owner;
            if (d.TryGetValue(t, out owner) && owner == me)
            {
                d.Remove(t);
            }
            if (d.Count == 0) claims.Remove(map);
        }

        /// <summary>把这个建筑的所有认领全放掉（断电/拆除/兜底）。</summary>
        public static void ReleaseAll(CompDigitalWorker me)
        {
            if (me == null) return;
            ReleaseAllIn(claims, me);
            ReleaseAllIn(cellClaims, me);
        }

        // ===================================================================
        // 格子表（播种这种"目标物还不存在"的活用）
        // ===================================================================

        /// <summary>这一格被哪个代理建筑认领了（没有/已失效则返回 null，并顺手清理失效项）。</summary>
        public static CompDigitalWorker CellOwnerOf(Map map, IntVec3 c)
        {
            if (map == null || !c.IsValid) return null;
            Dictionary<IntVec3, CompDigitalWorker> d;
            if (!cellClaims.TryGetValue(map, out d)) return null;
            CompDigitalWorker owner;
            if (!d.TryGetValue(c, out owner)) return null;
            if (owner == null || owner.parent == null || !owner.parent.Spawned)
            {
                d.Remove(c);
                return null;
            }
            return owner;
        }

        public static void TryClaimCell(Map map, IntVec3 c, CompDigitalWorker me)
        {
            if (map == null || !c.IsValid || me == null) return;
            ForCells(map)[c] = me;
        }

        /// <summary>只放掉**这一格**（并行时不能把别的活一起放了）。</summary>
        public static void ReleaseCell(Map map, IntVec3 c, CompDigitalWorker me)
        {
            if (map == null || !c.IsValid) return;
            Dictionary<IntVec3, CompDigitalWorker> d;
            if (!cellClaims.TryGetValue(map, out d)) return;
            CompDigitalWorker owner;
            if (d.TryGetValue(c, out owner) && owner == me)
            {
                d.Remove(c);
            }
            if (d.Count == 0) cellClaims.Remove(map);
        }

        /// <summary>把某一张表里属于 <paramref name="me"/> 的条目全清掉（两张表的清理逻辑共用）。</summary>
        private static void ReleaseAllIn<TKey>(Dictionary<Map, Dictionary<TKey, CompDigitalWorker>> table, CompDigitalWorker me)
        {
            if (table.Count == 0) return;
            List<Map> empty = null;
            foreach (KeyValuePair<Map, Dictionary<TKey, CompDigitalWorker>> kv in table)
            {
                List<TKey> mine = null;
                foreach (KeyValuePair<TKey, CompDigitalWorker> p in kv.Value)
                {
                    if (p.Value == me)
                    {
                        if (mine == null) mine = new List<TKey>();
                        mine.Add(p.Key);
                    }
                }
                if (mine != null)
                {
                    for (int i = 0; i < mine.Count; i++) kv.Value.Remove(mine[i]);
                }
                if (kv.Value.Count == 0)
                {
                    if (empty == null) empty = new List<Map>();
                    empty.Add(kv.Key);
                }
            }
            if (empty != null)
            {
                for (int i = 0; i < empty.Count; i++) table.Remove(empty[i]);
            }
        }
    }
}
