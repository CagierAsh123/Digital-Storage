using System;
using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Core;
using DigitalStorage.Effects;
using UnityEngine;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Components
{
    /// <summary>
    /// I5a+I5b: 自动收纳。Tick 扫 listerHaulables → 过滤 → 吸入自身**容器**。
    /// （3.0 是吸入账本；4.0 改投真实容器，见 TryIngest。两者都保留"入库瞬移"的产品决策。）
    /// I5b: 研究解锁 + Gizmo 开关 + 电力检查。
    /// I5c: 多级速率。
    /// </summary>
    public class CompAutoIngest : ThingComp
    {
        private Building_StorageCore core;
        private bool enabled = true;
        private int ingestRateCache = -1;
        private bool researchedCache;
        private int researchCheckTick = -1;

        /// <summary>
        /// L1: 刚取出标记表——按 thingID 记过期 tick（固定 300 tick 完整窗口），
        /// 不再每 120 tick 全清（旧实现窗口长度不确定，延迟拾取会被吞回）。
        /// </summary>
        private static readonly Dictionary<int, int> withdrawnUntil = new Dictionary<int, int>();

        /// <summary>
        /// <b>「工作台旁边那一圈」的首次出现时刻</b>（按 <c>thingIDNumber</c>）—— 见
        /// <see cref="IsInWaitingBillGiverRim"/>。存的刻意是**首次**而不是"最近一次"：
        /// 宽限期一到就必须放行，若每次扫描都刷新时刻，它就退化成"永久不收"了。
        /// </summary>
        private static readonly Dictionary<int, int> rimFirstSeen = new Dictionary<int, int>();

        /// <summary>
        /// 工作台旁边那一圈的宽限（tick）：给原版 / 搬运 mod 留出"正准备来拿它"的时间。
        /// 与 <see cref="MarkWithdrawn"/> 的 300 tick 取同一个量级（都是"一个 job 周期"）。
        /// </summary>
        private const int BillRimGraceTicks = 300;

        /// <summary>上面那张表多久没被再次算到就当它已经不在那儿了（<b>纯内存卫生</b>）。</summary>
        private const int RimFirstSeenKeepTicks = 60000;

        private static Thing[] candidateBuffer = new Thing[30];

        /// <summary>与 <see cref="candidateBuffer"/> 平行的目的地缓冲（收集时就判好，省一次重判）。</summary>
        private static Building_StorageCore[] candidateDestBuffer = new Building_StorageCore[30];

        /// <summary>
        /// 候选扫描的轮转起点。**必须有**：待搬表是个 <c>HashSet</c>，枚举顺序在集合不变时是稳定的，
        /// 固定从表头扫就会让排在后面的东西永远排不到（见 <see cref="CollectCandidates"/> 的注释）。
        /// </summary>
        private static int scanOffset;

        private static int lastSweepTick = -1;

        /// <summary>
        /// 换档 / 新开档时清掉所有跨档静态表（<b>B7 / 审计 2026-10-17</b>）。
        ///
        /// 这两张表都以 <c>thingIDNumber</c> 为键，而 <c>thingIDNumber</c> 是**每个存档各自从 1 开始**的
        /// 计数器 ⇒ 不清的话，上一档里"刚被取出、300 tick 内别吸回"的编号，会命中新档里
        /// <b>完全无关的另一件东西</b>，表现是"刚进档某些东西莫名其妙不被自动收纳"。
        /// 顺带也清轮转游标与清扫 tick，避免大编号残留导致扫描顺序异常。
        /// </summary>
        internal static void ClearSessionCaches()
        {
            withdrawnUntil.Clear();
            rimFirstSeen.Clear();
            scanOffset = 0;
            lastSweepTick = -1;
        }

        public static void MarkWithdrawn(Thing t)
        {
            if (t != null)
                withdrawnUntil[t.thingIDNumber] = Find.TickManager.TicksGame + 300;
        }

        public static bool IsRecentlyWithdrawn(Thing t)
        {
            return t != null && withdrawnUntil.TryGetValue(t.thingIDNumber, out int until)
                && Find.TickManager.TicksGame < until;
        }

        /// <summary>L1: 低频清扫过期项，防字典无界增长。</summary>
        public static void SweepWithdrawn()
        {
            int tick = Find.TickManager.TicksGame;
            if (tick - lastSweepTick < 2000) return;
            lastSweepTick = tick;
            var expired = new List<int>();
            foreach (var kv in withdrawnUntil)
                if (tick >= kv.Value) expired.Add(kv.Key);
            for (int i = 0; i < expired.Count; i++)
                withdrawnUntil.Remove(expired[i]);

            // 「工作台旁边」首次出现表：**只清很旧的**（清早了会让同一件东西重新开始计时，
            // 从而隔三差五地重新等一轮 —— 不影响正确性，但没必要）。
            var stale = new List<int>();
            foreach (var kv in rimFirstSeen)
                if (tick - kv.Value >= RimFirstSeenKeepTicks) stale.Add(kv.Key);
            for (int i = 0; i < stale.Count; i++)
                rimFirstSeen.Remove(stale[i]);
        }

        public bool Enabled
        {
            get => enabled;
            set => enabled = value;
        }

        public override void Initialize(CompProperties props)
        {
            base.Initialize(props);
            core = parent as Building_StorageCore;
            // 新建核心的 gizmo 开关跟随 Mod 设置默认值
            enabled = DigitalStorage.Settings.DigitalStorageSettings.autoIngestEnabled;
        }

        /// <summary>
        /// F10: 显式刷新研究缓存（旧实现把 ingestRateCache 的初始化藏在 IsResearched
        /// 的 getter 副作用里，调用顺序一变就会拿到 -1 → bufSize 为负 → 数组越界）。
        /// </summary>
        private void EnsureResearchCache()
        {
            int tick = Find.TickManager.TicksGame;
            if (tick == researchCheckTick && ingestRateCache > 0) return;

            researchCheckTick = tick;
            researchedCache = ResearchProjectDef.Named("DigitalStorage_AutoIngest1")?.IsFinished ?? false;
            if (ResearchProjectDef.Named("DigitalStorage_AutoIngest3")?.IsFinished ?? false) ingestRateCache = 10;
            else if (ResearchProjectDef.Named("DigitalStorage_AutoIngest2")?.IsFinished ?? false) ingestRateCache = 5;
            else ingestRateCache = 1;
        }

        public bool IsResearched
        {
            get
            {
                EnsureResearchCache();
                return researchedCache;
            }
        }

        /// <summary>诊断用（<see cref="AutoIngestDevTool"/>）：当前研究决定的每次吸入上限（1/5/10）。</summary>
        internal int IngestRate
        {
            get
            {
                EnsureResearchCache();
                return ingestRateCache > 0 ? ingestRateCache : 1;
            }
        }

        /// <summary>诊断用：候选扫描的轮转起点（见 <see cref="CollectCandidates"/>）。</summary>
        internal static int ScanOffset => scanOffset;

        public override void CompTick()
        {
            base.CompTick();
            if (core == null || !enabled || !core.Powered) return;

            // 设置「自动收纳」总开关（社区反馈：关掉后瞬移搬运就消失）
            if (!DigitalStorage.Settings.DigitalStorageSettings.autoIngestEnabled) return;

            int tick = Find.TickManager.TicksGame;

            // 每 15 tick 一次，用核心 ID 错开相位
            if ((tick + core.thingIDNumber) % 15 != 0) return;

            EnsureResearchCache();
            if (!researchedCache) return;

            var map = core.Map;
            if (map == null) return;

            // 装满了就别扫表：候选筛选里最贵的一步是"原版会不会把它搬进我这儿"
            // （WouldVanillaHaulIntoCore → 两次原版储存搜索），容量满了它必然回答"不会"，
            // 但每个候选仍要付一次搜索代价。装满时直接整轮省掉（其它核心各有自己的 comp）。
            if (core.innerContainer.Count >= core.maxStacks) return;

            SweepWithdrawn(); // L1: 清扫过期标记

            // M4: 候选收集不再按核心过滤器/容量过滤——被过滤的物品仍应路由到合格储存区，
            // 「核心吃不吃」的判断下沉到 RouteGroundItem 吞入分支。
            int rate = ingestRateCache > 0 ? ingestRateCache : 1;
            int taken = 0;

            // 收集候选项到静态小缓冲，避免 Ingest/Destroy 修改 haulables 列表导致迭代异常。
            int bufSize = rate * 3;
            if (candidateBuffer.Length < bufSize)
            {
                candidateBuffer = new Thing[bufSize];
                candidateDestBuffer = new Building_StorageCore[bufSize];
            }
            int bufCount = 0;


            // 候选来源：**原版自己的"待搬"表** —— `WorkGiver_Haul.PotentialWorkThingsGlobal`
            // 用的就是它（WorkGiver_Haul.cs:16）。于是"禁止 / 不可搬 / 已经在最优储存位置"
            // 全部由原版判定（`ListerHaulables.ShouldBeHaulable` → `StoreUtility.IsInValidBestStorage`），
            // 我们一行都不用重写；未开采的矿脉也天然不在表里（Mineable 的 alwaysHaulable 为
            // false ⇒ 没有搬运标记就不入表），所以连 `t is Mineable` 这种特例都不需要。
            //
            // 【曾走过的弯路，勿重蹈】一度换成 `listerThings.ThingsInGroup(HaulableEver)`，
            // 理由是"原版待搬表漏掉了躺在仓区里的交易白银"。那个判断是**错的**：
            // `IsInValidBestStorage` 用 `faction: Faction.OfPlayer` 去找"有没有更好的去处"，
            // 而核心走的是 `TryFindBestBetterNonSlotGroupStorageFor` 那条腿
            // （StoreUtility.cs:144 / :242），只要核心比当前储存更优，物品本来就在待搬表里。
            // 真正的元凶是当时那条活动区闸门（已删）。
            CollectCandidates(map, bufSize, ref bufCount);

            for (int i = 0; i < bufCount && taken < rate; i++)
            {
                Thing t = candidateBuffer[i];
                Building_StorageCore dest = candidateDestBuffer[i];
                // !t.Spawned：同相位（thingIDNumber % 15 相同）的另一个核心先把这件吸走了
                // ⇒ 它现在是别人的**容器内容物**。此时再 SplitOff 会把它从那个核心里拆出来
                // （B12：跨核心互相偷货）。候选集来自 listerHaulables（只含地上的东西），
                // 所以此刻"不在地上"= 已失效。
                if (t == null || t.Destroyed || !t.Spawned || dest == null || dest.Destroyed) continue;

                // 立刻入库（零延迟的产品决策不变）。光束只是**余像**：物品此刻已经进核心，
                // 所以在它被搬走之前先把原来的格子和绘制位置记下来交给光束。
                IntVec3 beamCell = t.PositionHeld;
                Vector3 beamPos = t.DrawPos;
                if (TryIngest(dest, t))
                {
                    taken++;
                    SpawnIngestBeam(map, beamCell, beamPos);
                }
            }
            // 清理引用防止 GC 泄漏
            for (int i = 0; i < bufCount; i++)
            {
                candidateBuffer[i] = null;
                candidateDestBuffer[i] = null;
            }
        }

        /// <summary>
        /// 把"**原版真的会搬进核心**"的东西填进 <see cref="candidateBuffer"/>（上限 <paramref name="bufSize"/>），
        /// 同时把目的核心写进 <see cref="candidateDestBuffer"/>。
        ///
        /// <para><b>⚠️ 这里修的是一个"东西永远不被收纳"的真 bug（2026-10-02 用户实测）。</b>
        /// 旧写法分两步：先把前 <c>bufSize</c> 件**通过过滤**的塞满缓冲，**之后**才判
        /// "原版会不会把它搬进核心"，不满足的就 <c>continue</c>。
        /// 于是那些"通过过滤、但原版不会搬进核心"的东西**每次都占着缓冲槽位，而且永远出不去**
        /// —— 待搬表是个 <c>HashSet</c>，枚举顺序在集合不变时稳定，所以只要表头长期压着
        /// <c>bufSize</c>(默认 30) 件这种东西，**排在它们后面的物品永远进不了缓冲区**，
        /// 表现为"有些东西死活不收纳"。而任何单件诊断都会说"它四层全通过" ——
        /// 因为**它每次被检查时确实都通过，问题是它从来没被检查过**。</para>
        ///
        /// <para>现在收集阶段就直接判目的地，缓冲里装的**全是可吸的**；
        /// 再叠一层<b>轮转扫描起点</b>兜底（万一某个窗口里的东西因为别的原因长期吸不动，
        /// 下一轮也会换一段扫描，不会把表尾饿死）。</para>
        /// </summary>
        private void CollectCandidates(Map map, int bufSize, ref int bufCount)
        {
            ICollection<Thing> haulables = map.listerHaulables.ThingsPotentiallyNeedingHauling();
            int total = haulables.Count;
            if (total <= 0) return;

            int start = scanOffset % total;
            if (start < 0) start = 0;
            scanOffset = (start + bufSize) % total;

            // 单轮扫描上限：待搬表很大且长时间无可吸件时（例如地上全是"已在更优储存"的东西），
            // 原先会把整张表扫一遍，每件都付出一次原版储存搜索的代价。
            // 上限只限制**这一轮**的工作量；轮转起点仍在推进 ⇒ 表尾不会被饿死（2026-10-02 那条教训）。
            int maxScanned = Math.Max(32, bufSize * 8);
            int scanned = 0;

            // 第一遍：从轮转起点扫到表尾
            int index = 0;
            foreach (Thing t in haulables)
            {
                if (index++ < start) continue;
                if (bufCount >= bufSize) return;
                if (scanned >= maxScanned) return;
                scanned++;
                TryAddCandidate(t, map, ref bufCount);
            }

            // 第二遍：绕回表头把缓冲补满（否则"起点靠后 + 可吸件数少"的这一轮会少吸）
            if (bufCount >= bufSize) return;
            index = 0;
            foreach (Thing t in haulables)
            {
                if (index++ >= start) break;
                if (bufCount >= bufSize) return;
                if (scanned >= maxScanned) return;
                scanned++;
                TryAddCandidate(t, map, ref bufCount);
            }
        }

        private void TryAddCandidate(Thing t, Map map, ref int bufCount)
        {
            if (t == null || t.Destroyed) return;
            if (!CanCollect(t, map)) return;

            // 路由判据：**原版自己会不会把它搬进核心**（见 WouldVanillaHaulIntoCore）。
            // 优先级比较、过滤器、容量全部由原版回答，不再由本 mod 复述一遍。
            Building_StorageCore dest = WouldVanillaHaulIntoCore(map, t);
            if (dest == null) return;

            candidateBuffer[bufCount] = t;
            candidateDestBuffer[bufCount] = dest;
            bufCount++;
        }

        /// <summary>
        /// 在物品**原来站的那一格**留一道收纳光束（纯余像）。物品这一刻已经进核心了，
        /// 光束<b>不承载任何逻辑</b>：拿不到 Def 或生成失败就直接没有任何效果，不影响功能。
        /// 由 <c>Mote_DS_IngestBeam</c>（Mote 子类）自己按 Def 的 mote 字段播完消失。
        /// </summary>
        private static void SpawnIngestBeam(Map map, IntVec3 cell, Vector3 drawPos)
        {
            ThingDef beamDef = DefDatabase<ThingDef>.GetNamedSilentFail("DS_IngestBeam");
            if (beamDef == null) return;

            Mote_DS_IngestBeam mote = GenSpawn.Spawn(beamDef, cell, map) as Mote_DS_IngestBeam;
            if (mote == null) return;

            mote.exactPosition = drawPos;
            mote.Init(Rand.Range(0f, 360f));
        }

        /// <summary>
        /// 入库瞬移：把一件地面物品直接搬进容器（产品决策：全部无损隔空获取，保留瞬移、零延迟）。
        ///
        /// 4.0 与 3.0 的差别只在"搬进哪儿"：3.0 是 <c>ledger.Ingest</c>（纯数据），
        /// 4.0 是 <c>innerContainer.TryAdd</c>（真实 Thing）—— 也因此这批东西
        /// 立刻能被原版看见（bill 取料 / 读数 / 出库）。
        ///
        /// <b>失败必须放回地面</b>，否则物品凭空消失。
        /// </summary>
        internal static bool TryIngest(Building_StorageCore core, Thing t)
        {
            if (core == null || t == null || t.Destroyed) return false;
            if (!core.Accepts(t)) return false;

            Map map = core.Map;
            if (map == null) return false;
            IntVec3 originalPos = t.PositionHeld;

            int take = Math.Min(t.stackCount, core.GetDirectlyHeldThings().GetCountCanAccept(t));
            if (take <= 0) return false;
            Thing taken = t.SplitOff(take);
            if (taken.Spawned) taken.DeSpawn();
            if (core.TryStore(taken, take) == take) return true;

            GenPlace.TryPlaceThing(taken, originalPos, map, ThingPlaceMode.Near);
            return false;
        }

        /// <summary>
        /// 候选被拒的原因（<b>只保留原版不回答的那几件事</b>）。过滤与诊断共用这一份判定
        /// （<see cref="RejectReason"/>），诊断绝不另写一套近似逻辑 —— 那样两边会漂移，
        /// 出现"诊断全绿但就是不收"的假象（这个坑本 mod 已经踩过一次）。
        ///
        /// <para>其余判据**全部交还原版**：禁止 / 不可搬 / 已在最优储存 → 由
        /// <c>listerHaulables</c> 的准入（<c>ShouldBeHaulable</c>）回答；
        /// 未开采矿脉 → 同上（Mineable 不入表）；优先级与容量 → 由
        /// <see cref="WouldVanillaHaulIntoCore"/> 里的原版目的地搜索回答。
        /// 本 mod 不再复述任何一条，也就不会和原版漂移。</para>
        /// </summary>
        internal enum Reject
        {
            None = 0,
            NullOrDestroyed,
            /// <summary>不在图上（在别人背包里 / 在某个容器里）—— 绝不能对它们 DeSpawn。</summary>
            NotOnMap,
            /// <summary>在关押区里（牢房 / 有囚犯的房间）—— 见 <see cref="IsInPrisonArea"/>。</summary>
            InPrisonArea,
            /// <summary>在工作台的**材料区**（= 工作台自己占的格子）—— 见 <see cref="IsOnBillGiver"/>。</summary>
            OnBillGiver,
            /// <summary>在"还在等料"的工作台**旁边一圈**里、还没过宽限 —— 见 <see cref="IsInWaitingBillGiverRim"/>。
            /// <b>默认不生效</b>（设置 <c>autoIngestYieldToJobs</c>）。</summary>
            OnBillGiverRim,
            Reserved,
            /// <summary>是某个 pawn 当前 / 排队中作业的目标 —— 见 <see cref="IsTargetOfAnyPawnJob"/>。
            /// <b>默认不生效</b>（设置 <c>autoIngestYieldToJobs</c>）。</summary>
            JobTarget,
            RecentlyWithdrawn
        }

        /// <summary>
        /// 候选过滤：只做原版不替我们回答的那几件事 —— 一条 <b>必须在地图上</b>的守卫
        /// （<c>listerHaulables</c> 里会出现"因过滤器变更而要从核心里搬出去"的内容物，
        /// 甲-1 的机制，对容器里的东西 <c>DeSpawn</c> 是错的）、**工作台材料区 / 关押区**两条豁免、
        /// **只读的预订检查**（不碰 <c>ReservationManager.Reserve</c>，与 MoreOrgans 的劳务手同款）、
        /// **"刚被取出"保护窗口**，以及 2026-10 加的两条（都是"东西正被别人用着"的形态，
        /// 第一条由设置 <c>autoIngestYieldToJobs</c> 控制、**默认关**，但装了 Pick Up And Haul 时强制开）：
        /// <see cref="Reject.JobTarget"/>（是某个 pawn 作业的目标）与
        /// <see cref="Reject.OnBillGiverRim"/>（躺在"还在等料"的工作台旁边、还没过宽限）。
        ///
        /// <para><b>为什么"被别人用着"的判据要放在这里</b>：入库是**瞬移**（零延迟、不派 job），
        /// 所以原版用来避免"抢别人东西"的那套机制（选目的地时问 <c>Accepts</c>、拿之前先
        /// <c>CanReserve</c>）在我们这条路上依次失效 —— 只要有一件东西"刚好在这个 tick 之前
        /// 落地、还没被预订、但某个作业已经指着它"，我们就会把它吸走，那个作业只能重来。
        /// 用户 2026-10 上报的「小人取出东西准备制造 → 材料立刻被吸走 → 反复取物卡住」
        /// 就是这一族（根因见 <see cref="IsInWaitingBillGiverRim"/> 的注释）。</para>
        ///
        /// <para>活动区闸门已于 4.0 删除：它误伤极大（挖矿掉落、拆建筑材料、交易掉在区外的
        /// 白银全在 Home 区之外），而它想防的"老远处石块"其实是未开采矿脉，现已由原版
        /// 待搬表天然挡住。用户既定方向是「不加任何限制、任何位置隔空取放」。</para>
        /// </summary>
        internal static Reject RejectReason(Thing t, Map map)
        {
            if (t == null || t.Destroyed) return Reject.NullOrDestroyed;
            // ⚠️ 判断"在图上的散落物"**只能用 t.Spawned**。
            // **绝不能**写 `t.ParentHolder != null` —— 已 Spawn 的东西 holder 就是 **Map 自己**的
            // ThingOwner（Thing.cs:1137-1140 正是在处理 `holdingOwner.Owner is Map` 这种状态），
            // 于是 ParentHolder **就是 Map、非 null**。用它会一次性把所有地面物品判成"不在图上"
            // （实测：待搬表 98 件全灭、自动收纳归零，而"通过=0"看起来还像"没东西可收"）。
            // 容器内 / 背包里的东西 Spawned == false，一条判断就够。
            if (!t.Spawned) return Reject.NotOnMap;
            if (IsOnBillGiver(t, map)) return Reject.OnBillGiver;
            // ★ 2026-10：作业已经指着它 —— 抢走 = 让那个作业找不到东西（见方法注释）。
            //   **默认不生效**（见设置 autoIngestYieldToJobs）：本 mod 的取向是"算中即入"，
            //   让位只是留给"万一起了取料死循环"时的一键对照。
            //   **例外**：装了 Pick Up And Haul 时无条件生效 —— 它一趟要捡七八堆，
            //   尾部的堆在 TryMakePreToilReservations 里常常没订上，被瞬移走就白跑一趟
            //   （依据见 Compatibility.PickUpAndHaulCompat 的三条研究结论）。
            //   生效时刻意排在预订检查**之前**：绝大多数 job 目标同时也被预订着，
            //   但"作业在手、预订还没写"与"落地的料根本没人预订"这两种情况只有这条能拦。
            if (Compatibility.PickUpAndHaulCompat.YieldToJobTargets
                && IsTargetOfAnyPawnJob(t, map)) return Reject.JobTarget;
            // 【曾经在这里加过一条 Reject.OnConstructionSite（工地 3×3 不收），已删除】
            // 当时我以为"送料到工地会落地、落地就会被吸走"。**这个前提是错的**：
            //   Frame : Building, IThingHolder（Frame.cs:11）带 resourceContainer，
            //   而 JobDriver_DS_Withdraw.PlaceHauled 本身优先放容器，落地只是"容器放不进"的兜底。
            //   用户实测（2026-10-02）：从没见过建造 pawn 把材料丢在地上，都是"蓝图→框架→进框架内胆"。
            // 代价却很大：3×3 邻域排除，碰上 1555 个蓝图的工地等于把一大片地图排除在收纳之外。
            // 真正的"建造后挂几秒 Wait"根因是预订冲突（见 JobDriver_DS_Withdraw.TryMakePreToilReservations）。
            // ⇒ 别再把它加回来，除非有人真的复现了"工地材料落地被吸走"。
            if (IsInPrisonArea(t, map)) return Reject.InPrisonArea;
            if (map.reservationManager.IsReserved(t)) return Reject.Reserved;
            if (IsRecentlyWithdrawn(t)) return Reject.RecentlyWithdrawn;

            // ★ 2026-10：别的都放行了，但它躺在"还在等料"的工作台旁边、还没躺够宽限 —— 再让一会儿。
            //   **默认不生效**（见设置 autoIngestYieldToJobs）。刻意放最后：这是唯一一条要额外花
            //   代价的判据（最多 8 次 GetEdifice），只想拦住"其余全通过"的那批，不想给每件候选加钱。
            if (DigitalStorage.Settings.DigitalStorageSettings.autoIngestYieldToJobs
                && IsInWaitingBillGiverRim(t, map)) return Reject.OnBillGiverRim;
            return Reject.None;
        }

        /// <summary>
        /// 这件东西是不是躺在**工作台的材料区**上。材料区 = 工作台**自己占用的格子**
        /// （<c>Building_WorkTable.cs:40</c>：<c>IngredientStackCells =&gt; GenAdj.CellsOccupiedBy(this)</c>）。
        ///
        /// <para><b>为什么必须排除</b>（用户实测：材料放在工作台材料区被自动收纳取走）：
        /// 原版把 bill 的原料就丢在这里，而本类的判据对它是成立的（在原版待搬表里 +
        /// 原版确实会把它搬进核心：核心是更优的容器）⇒ 料刚上台就被吸回核心。</para>
        ///
        /// <para>更糟的连锁：<c>WorkGiverUtility.HaulStuffOffBillGiverJob</c>（<c>WorkGiverUtility.cs:8-19</c>）
        /// 只要在材料区看到任何物品，就<b>不建 DoBill 作业</b>，改派"把台子上的东西搬走"的作业
        /// ⇒ 小人一趟趟把台子上的料搬去储存区，bill 永远开不了工。</para>
        ///
        /// <para>判据直接蹭原版自己的"材料区"定义，成本一次 <c>GetEdifice</c>。</para>
        ///
        /// <para><b>⚠️ 它只管工作台自己占的格子</b> —— "旁边一格"由
        /// <see cref="IsInWaitingBillGiverRim"/> 用宽限兜住（原版把料放上台子时
        /// <c>ThingPlaceMode.Near</c> 会往四周找格子，那些格子 <c>GetEdifice</c> 命中的是地板、
        /// 不是工作台）。两条一起才构成"材料区 + 它的边缘"。</para>
        /// </summary>
        private static bool IsOnBillGiver(Thing t, Map map)
        {
            Building edifice = t.PositionHeld.GetEdifice(map);
            return edifice is IBillGiver;
        }

        /// <summary>
        /// 这件东西是不是**某个已 spawn 的 pawn 当前（或排队中）作业的目标**。
        ///
        /// <para><b>为什么非有这条不可</b>：<c>ReservationManager.IsReserved</c> 只在**预订真正建立
        /// 之后**才为真，而"作业已经拿到它"与"预订写好"之间有一段窗口；更要命的是原版有几条链
        /// <b>根本不给掉在地上的东西预订</b>：<c>JobDriver_DoBill</c> 把原料放上台子走的是
        /// <c>SetTargetToIngredientPlaceCell</c> + <c>Toils_Haul.PlaceHauledThingInCell(..., storageMode: false)</c>
        /// （<c>JobDriver_DoBill.cs:128-130</c>），落到 <c>IngredientPlaceCell</c>，落不下就用
        /// <c>ThingPlaceMode.Near</c> 往旁边找格子 ⇒ 料站在工作台**占格之外**，
        /// 而<u>那件东西仍然是这个作业的 <c>targetB</c></u>（<c>CollectIngredientsToils</c> 把队列项
        /// 搬进 <c>targetB</c>，一直到配方做完都不摘；未完成品配方还要拿它算工时）。
        /// 这时把它瞬移进核心，作业就"找不到刚放手的那件料"，只能重新去取
        /// ⇒ 用户 2026-10 上报的「取出东西准备制造 → 材料立刻被吸走 → 反复取物卡住」。</para>
        ///
        /// <para><b>看四处</b>：<c>targetA/B/C</c>（<c>targetB</c> 就是正在用的那件料）、
        /// 两条 <c>targetQueue</c>（<c>A</c> / <c>B</c>，还没轮到取的"购物清单"；原版
        /// <c>Job</c> 没有 <c>targetQueueC</c>）、以及<b>排队中的 job</b>
        /// （原版与搬运 mod 都常先入队、手上的活干完再去）。<c>LocalTargetInfo.Thing</c>
        /// 对空格子返回 null，所以"格子目标"天然不命中。</para>
        ///
        /// <para><b>成本</b>：候选每轮 ≤ <c>rate*3</c>（研究满级 30）件，每件遍历本图已 spawn 的
        /// pawn 及其 job —— 全是引用比较、零分配。相比"吸错东西让小人反复取料"，这点开销可以忽略。</para>
        /// </summary>
        private static bool IsTargetOfAnyPawnJob(Thing t, Map map)
        {
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null || p.jobs == null) continue;

                if (JobTargets(t, p.jobs.curJob)) return true;

                JobQueue queue = p.jobs.jobQueue;
                if (queue == null) continue;
                for (int j = 0; j < queue.Count; j++)
                {
                    if (JobTargets(t, queue[j].job)) return true;
                }
            }
            return false;
        }

        private static bool JobTargets(Thing t, Job job)
        {
            if (job == null) return false;
            if (ReferenceEquals(job.targetA.Thing, t)) return true;
            if (ReferenceEquals(job.targetB.Thing, t)) return true;
            if (ReferenceEquals(job.targetC.Thing, t)) return true;

            // 只有 A / B 有队列（原版 Job 没有 targetQueueC）
            return TargetQueueContains(job.targetQueueA, t)
                || TargetQueueContains(job.targetQueueB, t);
        }

        private static bool TargetQueueContains(List<LocalTargetInfo> queue, Thing t)
        {
            if (queue == null) return false;
            for (int i = 0; i < queue.Count; i++)
            {
                if (ReferenceEquals(queue[i].Thing, t)) return true;
            }
            return false;
        }

        /// <summary>
        /// 这件东西是不是躺在**"还有 bill 在等料"的工作台旁边一圈**里，而且还没躺够
        /// <see cref="BillRimGraceTicks"/>。
        ///
        /// <para><b>它补的是 <see cref="IsOnBillGiver"/> 的边界</b>：那边只认工作台**自己占的格子**
        /// （<c>GetEdifice</c> 命中工作台）。而原版把料放上台子用的是
        /// <c>SetTargetToIngredientPlaceCell</c> + <c>PlaceHauledThingInCell</c>；材料位被相邻的堆占住时
        /// <c>ThingPlaceMode.Near</c> 会往四周找格子 ⇒ 料落在工作台**旁边一格**，那道闸门立刻失效，
        /// 而它在 15 tick 内就会被吸回核心 ⇒ 工作台永远缺料 ⇒ 小人反复取料
        /// （<c>JobDriver_DS_Withdraw.PlaceHauled</c> 也是同一套"先试 <c>IngredientStackCells</c>、
        /// 失败退回 <c>t.Position</c> + Near"的写法）。</para>
        ///
        /// <para><b>为什么是"宽限"而不是"硬排除"</b>：硬排除会让工作台周围的料永远清不掉，而玩家
        /// 的预期是"地图上的散落物自动进核心"。这里是"先让位 5 秒"：原版自己的
        /// <c>WorkGiver_DoBill</c> 会把地上的东西当原料捡走，搬运 mod 也来得及来拿；
        /// 5 秒过去还没人要 ⇒ 照旧收走。<b>只放宽一次</b>（<see cref="rimFirstSeen"/> 记的是首次
        /// 出现时刻、不刷新），所以不会退化成"永久不收"。</para>
        ///
        /// <para><b>为什么要求台子有 bill</b>：有 bill 才有"在等料"这回事。没 bill 的工作台
        /// 旁边的料就是普通散落物，不该为它付宽限。</para>
        ///
        /// <para><b>刻意不扩到工地（蓝图 / 框架）</b>：那条豁免 3.0 加过又删掉
        /// （见 <see cref="RejectReason"/> 里的注释，用户实测"建造工从不把材料丢在地上"，
        /// 而 3×3 排除会误伤极大片地图）。所以这里只认 <c>IBillGiver</c>。</para>
        /// </summary>
        private static bool IsInWaitingBillGiverRim(Thing t, Map map)
        {
            if (!HasWaitingBillGiverAround(t.PositionHeld, map)) return false;
            return !RimGraceElapsed(t);
        }

        /// <summary>这一格的 8 邻里有没有"有 bill 的"工作台（见 <see cref="IsInWaitingBillGiverRim"/>）。</summary>
        private static bool HasWaitingBillGiverAround(IntVec3 pos, Map map)
        {
            for (int i = 0; i < GenAdj.AdjacentCells.Length; i++)
            {
                IntVec3 c = pos + GenAdj.AdjacentCells[i];
                if (!c.InBounds(map)) continue;

                Building b = c.GetEdifice(map);
                if (b == null || b.Destroyed) continue;

                IBillGiver giver = b as IBillGiver;
                if (giver == null || giver.BillStack == null) continue;
                if (giver.BillStack.Count > 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 首次算到这一件（并开始计时）返回 false；从那时起过了 <see cref="BillRimGraceTicks"/> 才 true。
        /// <b>不刷新时刻</b>是有意的：宽限只放宽一次。
        /// </summary>
        private static bool RimGraceElapsed(Thing t)
        {
            int now = Find.TickManager.TicksGame;
            int first;
            if (!rimFirstSeen.TryGetValue(t.thingIDNumber, out first))
            {
                rimFirstSeen[t.thingIDNumber] = now;
                return false;
            }
            return now - first >= BillRimGraceTicks;
        }

        /// <summary>
        /// 这件东西是不是在**关押区**里。给囚犯送饭是"把饭丢在地上"：
        /// <c>WorkGiver_Warden_DeliverFood:49</c> 建 <c>DeliverFood</c> 作业，落点
        /// <c>job.targetC = RCellFinder.SpotToChewStandingNear(囚犯, 食物)</c>
        /// （<c>SpotToStandDuringJob</c> 只在囚犯**自己的 region** 内 4 格找），
        /// 而 <c>JobDriver_FoodDeliver:91</c> 直接 <c>TryDropCarriedThing</c> —— <b>不设禁止</b>。
        /// ⇒ 那坨饭会进原版待搬表、被核心当场吸走，囚犯永远吃不到，监狱喂食整体失效。
        ///
        /// <para>判据全部用**原版自己的概念**：房间是牢房（<c>Room.IsPrisonCell</c>，
        /// 由房内"关囚犯的床"推出），或房间里有囚犯（覆盖"有围墙但没屋顶、原版不认作
        /// prison cell"的牢房）。两种情况都不收。</para>
        ///
        /// <para>代价：真在牢房里的殖民地物资（误丢进去的钢材之类）也不会被吸走 ——
        /// 那是囚犯的私人空间，本来就不该被自动清空。</para>
        /// </summary>
        private static bool IsInPrisonArea(Thing t, Map map)
        {
            Room room = t.PositionHeld.GetRoom(map);
            if (room == null) return false; // 露天/过道：没有房间，无从判断
            if (room.IsPrisonCell) return true;

            // 原版同款写法（WorkGiver_Warden_DeliverFood.FoodAvailableInRoomTo:81-89）
            List<Region> regions = room.Regions;
            for (int i = 0; i < regions.Count; i++)
            {
                List<Thing> pawns = regions[i].ListerThings.ThingsInGroup(ThingRequestGroup.Pawn);
                for (int j = 0; j < pawns.Count; j++)
                {
                    Pawn p = pawns[j] as Pawn;
                    if (p != null && p.IsPrisoner) return true;
                }
            }
            return false;
        }

        private static bool CanCollect(Thing t, Map map)
        {
            return RejectReason(t, map) == Reject.None;
        }

        /// <summary>
        /// <b>收纳判据：原版自己会不会把这件东西搬进我们的核心。</b>
        ///
        /// <para>走原版 <c>StoreUtility.TryFindBestBetterNonSlotGroupStorageFor</c>
        /// （<c>StoreUtility.cs:242</c>）——**非格子型储存**就是容器那条腿，核心正在其中
        /// （它只跳过 <c>ISlotGroupParent</c> / <c>Building_Grave</c> / <c>!HaulDestinationEnabled</c>）。
        /// 因而"禁止 / 不可搬 / 已在最优储存"由候选集回答，"优先级 / 过滤器 / 容量 / 阵营 /
        /// 是否被禁"由原版回答，本 mod 一条都不复述。</para>
        ///
        /// <para><b>⚠️ 两个必须显式处理的点（否则核心会被静默跳过）：</b></para>
        /// <list type="number">
        /// <item><b><c>requiresDestReservation: false</c></b>。默认 <c>true</c> 时
        /// （<c>:285-305</c>）会要求目的地"可以被预约"：carrier 为空时退化成
        /// <c>IsReservedByAnyoneOf(thing, faction)</c> —— 核心是个热门卸货点、常被别的搬运工
        /// 预约着 ⇒ **整个核心被 continue 掉**。实测症状：56 件通过了过滤，却全被判
        /// "原版没有更好去处"（选中格子 <c>Invalid</c>、目的地 <c>null</c>），而核心明明
        /// <c>Accepts=True</c>、<c>收得下=3</c>。我们做的是瞬时入库：不派 job、不预约容器，
        /// 这条要求与我们无关。</item>
        /// <item><b>传 <c>carrier: null</c> 是安全的</b>：<c>:245</c> 只在
        /// <c>!t.SpawnedOrAnyParentSpawned</c> 时才解引用 carrier，而候选集里的东西都 Spawned；
        /// <c>:306-319</c> 的"可达性"整段在 carrier 为空时跳过 —— **正是我们要的**（隔空收纳，
        /// 不受距离限制）。不借任何殖民者 ⇒ 殖民者的 health / CanReserve / 位置都不会渗进结论。</item>
        /// </list>
        ///
        /// <para>另补一步原版保护：内层函数只看**容器之间**的关系，所以这里再问一次原版的
        /// 格子型储存，**若存在同级或更高优先级的格子可放，就让给格子**
        /// （保住用户 7.31 拍板的"别抢更高优先级储存"）。这条腿在空 carrier 下可能不可用，
        /// 用 try/catch 包住：宁可少一层优先级保护，也不能让整个收纳失效。</para>
        /// </summary>
        internal static Building_StorageCore WouldVanillaHaulIntoCore(Map map, Thing t)
        {
            if (map == null || t == null || t.Destroyed) return null;

            StoragePriority currentPriority = StoreUtility.CurrentStoragePriorityOf(t);
            if (!StoreUtility.TryFindBestBetterNonSlotGroupStorageFor(t, null, map, currentPriority,
                    Faction.OfPlayer, out IHaulDestination dest, acceptSamePriority: false,
                    requiresDestReservation: false))
            {
                return null; // 原版认为没有更好的容器可放（含：它已经在最优储存里）
            }

            Building_StorageCore core = dest as Building_StorageCore;
            if (core == null || core.Destroyed) return null;

            try
            {
                if (StoreUtility.TryFindBestBetterStoreCellFor(t, null, map, currentPriority,
                        Faction.OfPlayer, out IntVec3 cell, needAccurateResult: false))
                {
                    StoragePriority cellPriority = cell.GetSlotGroup(map).Settings.Priority;
                    if ((int)cellPriority >= (int)core.GetStoreSettings().Priority) return null;
                }
            }
            catch (Exception)
            {
                // 空 carrier 下这条腿可能不可用（它本是为有搬运工的场景写的）。忽略。
            }

            if (!core.Powered) return null; // 断电的核心原版也不会搬进去
            return core;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref enabled, "autoIngestEnabled", true);
        }
    }
}
