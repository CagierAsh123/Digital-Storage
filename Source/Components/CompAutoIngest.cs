using System;
using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Core;
using DigitalStorage.Effects;
using UnityEngine;
using RimWorld;
using Verse;

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
        private static Thing[] candidateBuffer = new Thing[30];

        /// <summary>与 <see cref="candidateBuffer"/> 平行的目的地缓冲（收集时就判好，省一次重判）。</summary>
        private static Building_StorageCore[] candidateDestBuffer = new Building_StorageCore[30];

        /// <summary>
        /// 候选扫描的轮转起点。**必须有**：待搬表是个 <c>HashSet</c>，枚举顺序在集合不变时是稳定的，
        /// 固定从表头扫就会让排在后面的东西永远排不到（见 <see cref="CollectCandidates"/> 的注释）。
        /// </summary>
        private static int scanOffset;

        private static int lastSweepTick = -1;

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
                if (t == null || t.Destroyed || dest == null || dest.Destroyed) continue;

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

            // 第一遍：从轮转起点扫到表尾
            int index = 0;
            foreach (Thing t in haulables)
            {
                if (index++ < start) continue;
                if (bufCount >= bufSize) return;
                TryAddCandidate(t, map, ref bufCount);
            }

            // 第二遍：绕回表头把缓冲补满（否则"起点靠后 + 可吸件数少"的这一轮会少吸）
            if (bufCount >= bufSize) return;
            index = 0;
            foreach (Thing t in haulables)
            {
                if (index++ >= start) break;
                if (bufCount >= bufSize) return;
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
        /// 候选被拒的原因（<b>只保留原版不回答的那两件事</b>）。过滤与诊断共用这一份判定
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
            Reserved,
            RecentlyWithdrawn
        }

        /// <summary>
        /// 候选过滤：只做两件原版不替我们回答的事 —— **只读的预订检查**（不碰
        /// <c>ReservationManager.Reserve</c>，与 MoreOrgans 的劳务手同款）与
        /// **"刚被取出"保护窗口**（防止玩家取出后立刻被吸回去），外加一条
        /// <b>必须在地图上</b>的守卫：<c>listerHaulables</c> 里会出现"因过滤器变更而要从
        /// 核心里搬出去"的内容物（甲-1 的机制），对容器里的东西 <c>DeSpawn</c> 是错的。
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
        /// </summary>
        private static bool IsOnBillGiver(Thing t, Map map)
        {
            Building edifice = t.PositionHeld.GetEdifice(map);
            return edifice is IBillGiver;
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
