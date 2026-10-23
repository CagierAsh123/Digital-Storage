using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 代理建筑干的"一件活"。
    ///
    /// <para>做法：**逐类照抄原版 <c>JobDriver</c> 的 <c>tickAction</c> / <c>tickIntervalAction</c>**，
    /// 把"走到目标"那一半扔掉。为什么不能直接跑原版 driver：
    /// <c>JobDriver.CurToil</c> / <c>HaveCurToil</c> 硬校验 <c>pawn.CurJob == job</c>
    /// （<c>Verse\JobDriver.cs</c>），脱离 job 的 driver 第一步就返回 null。</para>
    ///
    /// <para>速度**只认 <c>speedMult</c>**（= 建筑的等级倍率 0.8/1.2/2.0），刻意不乘
    /// <c>pawn.GetStatValue(...)</c> —— 理由见 <see cref="CompProperties_DigitalWorker.workSpeedMult"/>。
    /// 技能资质只用于品质与产量。</para>
    /// </summary>
    public abstract class DigitalTask
    {
        public Thing target;
        public CompDigitalWorker comp;

        /// <summary>这件活是哪一类工作找来的（并行配额按它计数）。由 comp 在认领时填。</summary>
        public WorkTypeDef workType;

        /// <summary>
        /// 任务自己要求"放弃这一件"（例如建造发现核心凑不齐料，要让路给原版搬运工）。
        /// ⚠️ 不要用 <c>comp.Release()</c> —— 那是"全放手"，并行时会连带杀掉别的活。
        /// </summary>
        public bool Abort;

        /// <summary>
        /// "干完一步"的计数（挖一镐 / 砍一刀 / 建一点）。comp 靠它决定要不要让手上那只手挥一下。
        /// 没有离散步骤的工作（例如将来的连续推进类）让它保持 0 即可，手就只待机呼吸。
        /// </summary>
        public int StrikeCount;

        /// <summary>
        /// 这类活趴在被加工目标上的是哪只手（Mote defName）。
        /// 拓展到种植/建造/清洁时：加一个换贴图的 Mote def，在对应适配器里重写本属性指过去即可。
        /// </summary>
        public virtual string HandMoteDefName
        {
            get { return "DS_WorkHand"; }
        }

        /// <summary>
        /// 每"干完一步"在目标处触发的特效（挖掘 = <c>EffecterDefOf.Mine</c>，每镐一次）。
        ///
        /// <para><b>由 comp 统一驱动</b>（不是任务自己 Spawn）：这样它才跟"手/读条"走同一套
        /// "只画前 N 件"的上限 —— 否则并行 200 时会出现"有特效但没有手"的错位。</para>
        /// </summary>
        public virtual EffecterDef HitEffecterDef
        {
            get { return null; }
        }

        /// <summary>面板/调试用的活名（挖掘 / 建造 / …）。</summary>
        public abstract string Label { get; }

        /// <summary>还能不能继续干（每 tick 问一次）。</summary>
        public abstract bool StillValid(Pawn pawn, Map map);

        /// <summary>干 1 tick 的活。<paramref name="speedMult"/> 已经乘进各类的算式里。</summary>
        public abstract void Work(Pawn pawn, Map map, float speedMult);

        public virtual bool Finished
        {
            get { return Abort || target == null || target.Destroyed || !target.Spawned; }
        }

        /// <summary>0~1 的进度（给建筑底下那根黄色读条用）；&lt;0 表示这类活没有进度概念。</summary>
        public virtual float Progress01
        {
            get { return -1f; }
        }

        /// <summary>放手时收掉自己挂的 effecter 等资源（断电/拆除/目标失效/干完都会调）。</summary>
        public virtual void Cleanup()
        {
        }

        /// <summary>
        /// <b>格子型活</b>的目标格（默认 = 目标物所在格）。
        ///
        /// <para>为什么需要：原版有几个 WorkGiver 是 <b>scanCells 型</b> —— 候选是格子不是 Thing，
        /// 闸门与取活都在 <c>JobOnCell</c>/<c>HasJobOnCell</c> 里（<c>WorkGiver_GrowerSow</c> /
        /// <c>WorkGiver_GrowerHarvest</c>）。而"播种"要种的那棵苗此刻还<b>不存在</b>
        /// ⇒ 这类活没有 <c>target</c>，只有一个格子。表现层（手 / 黄色读条）与认领都按它走。</para>
        /// </summary>
        public virtual IntVec3 TargetCell
        {
            get { return (target != null && target.Spawned) ? target.Position : IntVec3.Invalid; }
        }

        /// <summary>
        /// 认领这一件活。Thing 型 = 占住目标物；<b>格子型改写本方法去占格子</b>
        /// （播种的苗还不存在，没有 Thing 可以占）。与 <see cref="Release"/> 必须成对。
        /// </summary>
        public virtual void Claim(Map map, CompDigitalWorker me)
        {
            DigitalWorkerClaims.TryClaim(map, target, me);
        }

        /// <summary>放手这一件活（清掉自己那份认领）。</summary>
        public virtual void Release(Map map, CompDigitalWorker me)
        {
            DigitalWorkerClaims.Release(map, target, me);
        }

        public virtual string TargetLabel
        {
            get
            {
                if (target != null) return target.LabelShort;
                IntVec3 c = TargetCell;
                return c.IsValid ? "(" + c.x + ", " + c.z + ")" : "?";
            }
        }
    }

    /// <summary>一种工作类型的适配器：绑定"哪个原版 <see cref="WorkGiver"/> 负责找这种活"。</summary>
    public abstract class DigitalTaskAdapter
    {
        /// <summary>对应的原版 WorkGiver 类（用 <c>IsAssignableFrom</c> ⇒ 子类/mod 扩展也算）。</summary>
        public abstract Type WorkGiverClass { get; }

        /// <summary>
        /// <b>这个适配器只认这几族工作类型</b>（<see cref="WorkTypeDef.defName"/>；返回 null = 不限制）。
        ///
        /// <para><b>为什么非有这条不可</b>：<see cref="DigitalTaskRegistry.FindGivers"/> 只用
        /// <c>WorkGiverDef.workType</c> 问"这个工作类型下有哪些 giver"，而 <see cref="WorkGiverClass"/>
        /// 是用 <c>IsAssignableFrom</c> 认的 ⇒ <b>"某族活的 giver 被塞进别的工作类型"</b>时，
        /// 那族适配器就会在那个工作类型下被启用。<b>2026-10 用户上报的「种植代理可以建造」正是这么来的</b>：
        /// 只要有一个"建造族"giver（<c>WorkGiver_ConstructFinishFrames</c> 或其派生类）落在
        /// <c>Growing</c> / <c>PlantCutting</c> 下（"把地形当蓝图建"的整地 / 农地类 mod 很容易这么写），
        /// 种植代理就会把它当建造活干。原版 Core 的 Growing / PlantCutting 里**没有**这种 giver
        /// （见 <c>WorkGiverDefs</c>：只有 Replant / GrowerHarvest / GrowerSow / PlantSeed / ExtractTree / PlantsCut），
        /// 所以只有装了那类 mod 才会出现 —— 本机自然复现不出来。</para>
        ///
        /// <para>这一条把"哪个适配器能在哪个工作类型下跑"从<b>隐式</b>（只看 giver 的类）变成<b>显式声明</b>：
        /// 适配器的族 × 建筑配置的 <c>workTypes</c> 必须相交，否则连候选都不生成
        /// ⇒ <b>"种植代理不可能建造"成为结构保证</b>，与玩家装了什么 mod 无关。</para>
        ///
        /// <para>清单里顺带列了 Complex Jobs 的拆分类工作类型（<c>FSFHarvesting</c> / <c>FSFDeconstruct</c>）：
        /// 不装该 mod 时这些 defName 根本不存在，列着零成本。<b>2026-10-20 起不必再手工往建筑 XML 里补了</b>：
        /// <see cref="FollowedWorkTypes"/> 会自动跟上"canonical giver 被搬去的工作类型"
        /// （旧实现要"适配器清单 + 建筑 workTypes 两处都放开才生效"，等于没生效）。</para>
        /// </summary>
        public virtual string[] HomeWorkTypes
        {
            get { return null; }
        }

        /// <summary>
        /// <paramref name="workType"/> 下允许用本适配器吗 = <b>按名字认</b>（见 <see cref="HomeWorkTypes"/>）
        /// **或**落在 <see cref="FollowedWorkTypes"/> 里（giver 被别的 mod 搬去了别的工作类型）。
        /// </summary>
        public bool AcceptsWorkType(WorkTypeDef workType)
        {
            if (AcceptsByHomeName(workType)) return true;

            // ★ 跟随：canonical giver 现在实际挂在哪个工作类型下，就也认哪个。
            //   没有这条时，Complex Jobs 一类"把 giver 搬去 FSF* 工作类型"的 mod 会让拆除/收割**静默失效**
            //   （giver 还在，只是不在建筑配置的那个 workType 下 ⇒ FindGivers 永远返回空）。
            List<WorkTypeDef> follow = FollowedWorkTypes;
            for (int i = 0; i < follow.Count; i++)
            {
                if (follow[i] == workType) return true;
            }
            return false;
        }

        /// <summary>只按 <see cref="HomeWorkTypes"/> 里的 <c>defName</c> 认（跟随集不算）。</summary>
        public bool AcceptsByHomeName(WorkTypeDef workType)
        {
            string[] home = HomeWorkTypes;
            if (home == null) return true;
            if (workType == null || workType.defName == null) return false;
            for (int i = 0; i < home.Length; i++)
            {
                if (home[i] == workType.defName) return true;
            }
            return false;
        }

        private List<WorkTypeDef> followedWorkTypes;

        /// <summary>
        /// <b>这个适配器负责的那族 giver 现在实际挂在哪些工作类型下</b>（懒算一次，def 表加载后才有值）。
        ///
        /// <para><b>为什么需要</b>：我们的实现是"借原版 WorkGiver 找活"，而 <c>WorkGiverDef.workType</c>
        /// 是**可被 mod 改的**。Complex Jobs 就把 <c>Deconstruct</c> 从 <c>Construction</c> 搬到了
        /// <c>FSFDeconstruct</c>、把 <c>GrowerHarvest</c> 搬到 <c>FSFHarvesting</c> ——
        /// giver 类一个字没变，只是不挂在建筑配置的工作类型下了 ⇒ <see cref="DigitalTaskRegistry.FindGivers"/>
        /// 返回空集、**一条日志都没有**（2026-10 wiki 已登记为"静默失效"，本轮修掉）。</para>
        ///
        /// <para><b>为什么用"精确类相等"而不是 <c>IsAssignableFrom</c></b>：
        /// 后者会把**别的 mod 的派生 giver**也算进来 —— 那正是「种植代理可以建造」的来源
        /// （某整地/农地类 mod 的 <c>WorkGiver_ConstructFinishFrames</c> 派生类挂在 <c>PlantCutting</c> 下）。
        /// 只认"canonical def 自己的类"⇒ 只跟随"原版那条被搬家"，不跟随"外来派生类"。</para>
        /// </summary>
        public List<WorkTypeDef> FollowedWorkTypes
        {
            get
            {
                if (followedWorkTypes != null) return followedWorkTypes;

                List<WorkTypeDef> list = new List<WorkTypeDef>();
                Type cls = WorkGiverClass;
                if (cls != null)
                {
                    List<WorkGiverDef> all = DefDatabase<WorkGiverDef>.AllDefsListForReading;
                    for (int i = 0; i < all.Count; i++)
                    {
                        WorkGiverDef d = all[i];
                        if (d == null || d.workType == null) continue;
                        if (d.giverClass != cls) continue;
                        if (!list.Contains(d.workType)) list.Add(d.workType);
                    }
                }
                followedWorkTypes = list;
                return list;
            }
        }

        public abstract DigitalTask MakeTask(Thing t, CompDigitalWorker comp);

        /// <summary>
        /// 这个 giver 是不是 <b>scanCells 型</b>（候选与闸门都在格子上，例如播种 / 自动收割）。
        ///
        /// <para>置 true ⇒ comp 走格子分支：候选集用 <see cref="CandidateCells"/>、取活用
        /// <see cref="MakeCellTask"/>，并且<b>不问</b> <c>HasJobOnThing</c> ——
        /// <c>WorkGiver_Scanner.HasJobOnThing</c> 的实现是 <c>JobOnThing(...) != null</c>，
        /// 而 cell 型 giver 不重写 <c>JobOnThing</c> ⇒ 用 Thing 那套去问，答案恒为 false。</para>
        /// </summary>
        public virtual bool CellBased
        {
            get { return false; }
        }

        /// <summary>
        /// 格子型候选集。默认直接用原版的 <c>PotentialWorkCellsGlobal</c>
        /// （**不要自己发明扫描规则**：种植区/种植盆的范围、季节、光照、<c>allowSow</c> 那道闸门
        /// 全在里面）。返回的是惰性序列，comp 会物化并缓存一次扫描。
        /// </summary>
        public virtual IEnumerable<IntVec3> CandidateCells(Map map, Pawn pawn, WorkGiver_Scanner scanner)
        {
            return scanner.PotentialWorkCellsGlobal(pawn);
        }

        /// <summary>
        /// 格子型取活：自己问原版（<c>JobOnCell</c> / <c>HasJobOnCell</c>），过不了就返回 null。
        /// 认领/失效判定由返回的 <see cref="DigitalTask"/> 自己负责。
        /// </summary>
        public virtual DigitalTask MakeCellTask(IntVec3 cell, Map map, Pawn pawn, WorkGiver_Scanner scanner, CompDigitalWorker comp)
        {
            return null;
        }

        /// <summary>找活前的廉价预筛（可选）。</summary>
        public virtual bool CanTarget(Pawn pawn, Thing t)
        {
            return true;
        }

        /// <summary>
        /// <b>每 tick 一次的机会：把昂贵的候选集刷新"分帧"推进</b>（默认什么都不做）。
        ///
        /// <para>给谁用：候选集本身要 O(全图) 才能算出来的适配器（目前只有清洁 ——
        /// <c>ThingRequestGroup.Filth</c> 拿不到专用 lister，只能过滤 <c>AllThings</c>）。
        /// 一次扫完是 3~15ms 的尖峰；按索引每 tick 走一小片，尖峰变成常量。</para>
        /// </summary>
        public virtual void SliceTick(Map map, Pawn pawn, WorkGiver_Scanner scanner)
        {
        }

        /// <summary>
        /// 是否信任原版 <c>WorkGiver.HasJobOnThing</c> 作为闸门（默认信任）。
        ///
        /// <para>不信任的场合只有一种：<b>原版闸门里含"可达性"</b>，而代理建筑是**隔空**干活的。
        /// 目前只有清洁需要自备候选集（原版 <c>WorkGiver_CleanFilth</c> 的候选集来自
        /// <c>listerFilthInHomeArea</c>，而我们拍板"放宽 Home 区"）⇒ 它同时不信任候选集与闸门，
        /// 改为在 <c>CanTarget</c>/<c>StillValid</c> 里自己做同样的检查。</para>
        /// </summary>
        public virtual bool TrustWorkGiver
        {
            get { return true; }
        }

        /// <summary>
        /// 候选集。默认走原版（<c>scanner.PotentialWorkThingsGlobal</c>，为空则退到
        /// <c>listerThings.ThingsMatching(PotentialWorkThingRequest)</c>）；需要放宽原版范围时重写。
        /// </summary>
        public virtual IEnumerable<Thing> CandidateSet(Map map, Pawn pawn, WorkGiver_Scanner scanner)
        {
            IEnumerable<Thing> set = null;
            try
            {
                set = scanner.PotentialWorkThingsGlobal(pawn);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 取代理候选集失败：" + scanner.def.defName + " → " + e,
                    scanner.def.shortHash * 31 + 7717);
            }
            if (set == null)
            {
                set = map.listerThings.ThingsMatching(scanner.PotentialWorkThingRequest);
            }
            return set;
        }
    }

    /// <summary>
    /// 适配器注册表。**白名单**：没登记的工作类型，代理建筑直接视而不见（也省掉它的候选扫描）。
    /// 已有：挖掘 / 建造 / 拆除 / 清洁 / 伐木(割除+收获标记) / 播种 / 自动收割。
    /// </summary>
    public static class DigitalTaskRegistry
    {
        private static readonly List<DigitalTaskAdapter> All = new List<DigitalTaskAdapter>
        {
            new DigitalTaskAdapter_Mine(),
            new DigitalTaskAdapter_Construct(),
            new DigitalTaskAdapter_Deconstruct(),
            new DigitalTaskAdapter_Clean(),
            new DigitalTaskAdapter_PlantCut(),
            new DigitalTaskAdapter_GrowerSow(),
            new DigitalTaskAdapter_GrowerHarvest(),
        };

        private static readonly Dictionary<WorkTypeDef, List<WorkGiver>> giversCache =
            new Dictionary<WorkTypeDef, List<WorkGiver>>();

        /// <summary>
        /// 这个工作类型下**所有有适配器**的 WorkGiver（按 Defs 加载顺序）。
        ///
        /// <para>⚠️ 一个工作类型可以有**多个** WorkGiver，必须全都要试：
        /// <c>Construction</c> 下就有 建造(<c>ConstructFinishFrames</c>)、拆除(<c>Deconstruct</c>)、
        /// 维修(<c>Repair</c>)、修屋顶…；早期版本只取"第一个有适配器的"，结果
        /// **建造代理永远不拆建筑**（那一类活根本没被问过）。</para>
        /// </summary>
        public static List<WorkGiver> FindGivers(WorkTypeDef workType)
        {
            if (workType == null) return null;

            List<WorkGiver> cached;
            if (giversCache.TryGetValue(workType, out cached)) return cached;

            List<WorkGiver> found = new List<WorkGiver>();
            List<WorkGiverDef> all = DefDatabase<WorkGiverDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                WorkGiverDef d = all[i];
                if (d == null || d.workType != workType) continue;
                WorkGiver w = d.Worker;
                if (w == null) continue;

                // ⚠️ 两道门都要过：① 有适配器（按 giverClass 认）；② 那个适配器**允许在这个工作类型下跑**
                //    （见 DigitalTaskAdapter.HomeWorkTypes —— 它就是"种植代理可以建造"那个 bug 的根治：
                //    别的 mod 把建造族 giver 塞进 Growing 也不会被我们接）。只有第 ① 道的话，
                //    "giver 被塞错工作类型"就会直接把活派给不对的代理建筑。
                DigitalTaskAdapter adapter = AdapterFor(w);
                if (adapter == null || !adapter.AcceptsWorkType(workType)) continue;

                found.Add(w);
            }
            giversCache[workType] = found;
            return found;
        }

        /// <summary>
        /// 把建筑配置的工作类型展开成"实际要扫的工作类型"= 配置项 ∪ 各适配器的
        /// <see cref="DigitalTaskAdapter.FollowedWorkTypes"/>（只对"按名字认这个配置项"的适配器展开）。
        ///
        /// <para><b>为什么在运行时展开而不是写进 XML</b>：跟随是**运行时**才知道的（要看别人把 giver 挪去了哪），
        /// XML 是静态的；而且跟随集为空时展开结果恰好等于配置项，零风险。展开后
        /// <c>TryScanWorkType</c> 会多问几个工作类型，<c>FindGivers</c> 有缓存，成本可忽略。</para>
        /// </summary>
        public static List<WorkTypeDef> ExpandFollowed(List<WorkTypeDef> configured)
        {
            List<WorkTypeDef> list = new List<WorkTypeDef>();
            if (configured != null)
            {
                for (int i = 0; i < configured.Count; i++)
                {
                    WorkTypeDef wt = configured[i];
                    if (wt != null && !list.Contains(wt)) list.Add(wt);
                }
            }

            int configuredCount = list.Count;   // 只对"配置来的"展开，避免跟随集自己再放大
            for (int i = 0; i < configuredCount; i++)
            {
                WorkTypeDef wt = list[i];
                for (int a = 0; a < All.Count; a++)
                {
                    DigitalTaskAdapter adapter = All[a];
                    if (!adapter.AcceptsByHomeName(wt)) continue;
                    List<WorkTypeDef> follow = adapter.FollowedWorkTypes;
                    for (int f = 0; f < follow.Count; f++)
                    {
                        if (!list.Contains(follow[f])) list.Add(follow[f]);
                    }
                }
            }
            return list;
        }

        public static DigitalTaskAdapter AdapterFor(WorkGiver giver)
        {
            if (giver == null || giver.def == null || giver.def.giverClass == null) return null;
            for (int i = 0; i < All.Count; i++)
            {
                if (All[i].WorkGiverClass.IsAssignableFrom(giver.def.giverClass)) return All[i];
            }
            return null;
        }

        /// <summary>
        /// 原版 <c>JobGiver_Work.PawnCanUseWorkGiver</c> 的节选（它是 private，只能照抄）。
        ///
        /// <para><b>刻意跳过两道</b>：<c>WorkTypeIsDisabled</c> 与 <c>WorkTagIsDisabled</c>。
        /// 原因是我们的工人是**临时生成的 colonist**，会带随机背景与特质，
        /// 而 <c>GetDisabledWorkTypes</c> 会把背景的禁用工作类型算进去（<c>Pawn.cs:4251-4260</c>）——
        /// 机器不该因为抽到"不能做熟练劳动"就罢工。**代理建筑本身就是许可**。
        /// 其余三道照旧：<c>nonColonistsCanDo</c> / <c>ShouldSkip</c> / <c>MissingRequiredCapacity</c>。</para>
        /// </summary>
        public static bool PawnCanUse(WorkGiver giver, Pawn pawn, DigitalTaskAdapter adapter)
        {
            if (giver == null || giver.def == null || pawn == null) return false;
            if (!giver.def.nonColonistsCanDo && !pawn.IsColonist
                && !(pawn.RaceProps.IsMechanoid && pawn.Faction == Faction.OfPlayer))
            {
                return false;
            }

            // ⚠️ `ShouldSkip` 必须跟着 `TrustWorkGiver` 一起放行：
            // WorkGiver_CleanFilth.ShouldSkip = "家区里没有污物就跳过整类工作"
            // （WorkGiver_CleanFilth.cs:22-25）—— 我们放宽了候选集（全图污物），
            // 却仍被这道门拦死，实测表现就是"清洁代理一件活都不干"。
            if (adapter == null || adapter.TrustWorkGiver)
            {
                if (giver.ShouldSkip(pawn)) return false;
            }

            if (giver.MissingRequiredCapacity(pawn) != null) return false;
            if (pawn.RaceProps.IsMechanoid && !giver.def.canBeDoneByMechs) return false;
            return true;
        }
    }

    // ==========================================================================================
    // 挖掘 —— 照抄 JobDriver_Mine.DoDamage / ResetTicksToPickHit
    // ==========================================================================================
    public class DigitalTaskAdapter_Mine : DigitalTaskAdapter
    {
        /// <summary>只认 Mining（见 <see cref="DigitalTaskAdapter.HomeWorkTypes"/>）。</summary>
        private static readonly string[] Homes = { "Mining" };

        public override Type WorkGiverClass
        {
            get { return typeof(WorkGiver_Miner); }
        }

        public override string[] HomeWorkTypes
        {
            get { return Homes; }
        }

        public override DigitalTask MakeTask(Thing t, CompDigitalWorker comp)
        {
            return new DigitalTask_Mine { target = t, comp = comp };
        }
    }

    public class DigitalTask_Mine : DigitalTask
    {
        /// <summary>原版 <c>JobDriver_Mine.ResetTicksToPickHit</c>：<c>round(100 / MiningSpeed)</c>。
        /// 这里把 <c>MiningSpeed</c> 换成建筑的速度倍率（拍板：速度只认倍率）。</summary>
        private const int BaseTicksBetweenPickHits = 100;

        private const int BaseDamagePerPickHit_NaturalRock = 80;
        private const int BaseDamagePerPickHit_NotNaturalRock = 40;

        private float ticksToPickHit = -1f;

        public override string Label
        {
            get { return "DS_Task_Mine".Translate().ToString(); }
        }

        /// <summary>原版 <c>JobDriver_Mine</c> 用的就是 <c>EffecterDefOf.Mine</c>；由 comp 在表现层驱动。</summary>
        public override EffecterDef HitEffecterDef
        {
            get { return EffecterDefOf.Mine; }
        }

        /// <summary>与原版 <c>JobDriver_Mine</c> 的读条口径一致：<c>1 - HitPoints/MaxHitPoints</c>。</summary>
        public override float Progress01
        {
            get
            {
                if (target == null || target.MaxHitPoints <= 0) return -1f;
                return 1f - (float)target.HitPoints / (float)target.MaxHitPoints;
            }
        }

        public override void Cleanup()
        {
        }

        public override bool StillValid(Pawn pawn, Map map)
        {
            if (target == null || target.Destroyed || !target.Spawned || !target.def.mineable) return false;
            DesignationManager dm = map.designationManager;
            return dm.DesignationAt(target.Position, DesignationDefOf.Mine) != null
                || dm.DesignationAt(target.Position, DesignationDefOf.MineVein) != null;
        }

        public override void Work(Pawn pawn, Map map, float speedMult)
        {
            // 铁律：产出函数没有 Destroyed 守卫，重复调 = 重复掉产物
            if (target == null || target.Destroyed || !target.Spawned) return;

            if (ticksToPickHit < 0f)
            {
                ticksToPickHit = Mathf.Round(BaseTicksBetweenPickHits / speedMult);
            }

            ticksToPickHit -= 1f;
            if (ticksToPickHit > 0f) return;

            // 原版 JobDriver_Mine.cs:62-66 是"先触发特效再结算伤害"。
            // ⚠️ 但我们**不在这里 Spawn effecter**：特效改由 comp 在表现层驱动，
            //    这样它才和"手 / 黄色读条"共用同一套"只画前 N 件"的上限 ——
            //    否则并行 200 时会出现"有特效，但那只手上没人"的错位（实测报过）。
            //    另外也刻意不照抄 `Trigger(actor, mineTarget)`：actor 是假 pawn，
            //    sprayer 会去读 `TargetInfo.CenterVector3` → `Pawn.DrawPos`（历史上 NRE 过）。
            StrikeCount++;

            DoDamage(pawn, map);

            if (target != null && !target.Destroyed)
            {
                ticksToPickHit = Mathf.Round(BaseTicksBetweenPickHits / speedMult);
            }
        }

        /// <summary>
        /// 原样搬 <c>JobDriver_Mine.DoDamage</c>：最后一镐走"手动清零 + <c>DestroyMined</c>"，
        /// 而不是 <c>TakeDamage</c> 打死它 —— 后者走 <c>Mineable.Destroy(KillFinalize)</c> 分支时
        /// pawn 为 null，产物会被自动 Forbid（原版 <c>JobDriver_Mine.cs:112-115</c> 同款处理）。
        /// </summary>
        private void DoDamage(Pawn pawn, Map map)
        {
            Thing t = target;
            int num = (t.def.building != null && t.def.building.isNaturalRock)
                ? BaseDamagePerPickHit_NaturalRock
                : BaseDamagePerPickHit_NotNaturalRock;

            Mineable mineable = t as Mineable;
            if (mineable == null || t.HitPoints > num)
            {
                t.TakeDamage(new DamageInfo(DamageDefOf.Mining, num, 0f, -1f, pawn));
                return;
            }

            // 最后一镐：受"每 tick 完成预算"约束。拿不到票就留着这点血，下一 tick 再收尾
            // （产物/掉落/记录完全走原版，只是晚一 tick）。
            if (!DigitalWorkBudget.AllowCompletion()) return;

            bool isMineVein = map.designationManager.DesignationAt(mineable.Position, DesignationDefOf.MineVein) != null;
            IntVec3 pos = mineable.Position;

            mineable.Notify_TookMiningDamage(t.HitPoints, pawn);
            mineable.HitPoints = 0;
            mineable.DestroyMined(pawn);

            if (pawn.records != null)
            {
                pawn.records.Increment(RecordDefOf.CellsMined);
            }
            if (map.mineStrikeManager != null)
            {
                map.mineStrikeManager.CheckStruckOre(pos, t.def, pawn);
            }
            if (isMineVein)
            {
                IntVec3[] adjacent = GenAdj.AdjacentCells;
                for (int i = 0; i < adjacent.Length; i++)
                {
                    Designator_MineVein.FloodFillDesignations(pos + adjacent[i], map, t.def);
                }
            }
        }
    }

    // ==========================================================================================
    // 建造 —— 照抄 JobDriver_ConstructFinishFrame 的 tickAtomicAction + 数字存储独有的"补料"
    //
    // 原版耦合点：蓝图 → Frame 不是建造工干的，只能靠搬运链的收尾 toil
    // （JobDriver_HaulToContainer.cs:178 → Toils_Construct.MakeSolidThingFromBlueprintIfNecessary
    //  → Blueprint.TryReplaceWithSolidThing，public virtual）。
    // More Organs 当年只能做"材料已在框里"的 Frame（R4：CompleteConstruction 会无条件
    // ClearAndDestroyContents）。数字存储的核心能瞬间供料，所以这里**连送料一起做** ⇒ 真·全自动建造。
    // ==========================================================================================
    public class DigitalTaskAdapter_Construct : DigitalTaskAdapter
    {
        /// <summary>只认 Construction（见 <see cref="DigitalTaskAdapter.HomeWorkTypes"/>）。</summary>
        private static readonly string[] Homes = { "Construction" };

        public override Type WorkGiverClass
        {
            get { return typeof(WorkGiver_ConstructFinishFrames); }
        }

        public override string[] HomeWorkTypes
        {
            get { return Homes; }
        }

        /// <summary>
        /// 不信任原版闸门：<c>WorkGiver_ConstructFinishFrames.JobOnThing</c> 会走
        /// <c>GenConstruct.CanConstruct</c>，而它含**可达性**判定（CanTouchTargetFromValidCell /
        /// CanReserveAndReach）—— 代理建筑是隔空干活的，照抄那道门就永远不干活。
        /// 技能/意识/阻塞那几项由 <see cref="DigitalTask_Construct.StillValid"/> 自己查。
        /// </summary>
        public override bool TrustWorkGiver
        {
            get { return false; }
        }

        public override DigitalTask MakeTask(Thing t, CompDigitalWorker comp)
        {
            return new DigitalTask_Construct { target = t, comp = comp };
        }

        public override IEnumerable<Thing> CandidateSet(Map map, Pawn pawn, WorkGiver_Scanner scanner)
        {
            List<Thing> blueprints = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint);
            List<Thing> frames = map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame);
            List<Thing> all = new List<Thing>(blueprints.Count + frames.Count);
            all.AddRange(blueprints);
            all.AddRange(frames);
            return all;
        }

        public override bool CanTarget(Pawn pawn, Thing t)
        {
            // 只认建筑蓝图（Blueprint_Build）：安装蓝图（Blueprint_Install）要的是"小化的建筑"，
            // 不是钢/木料，供料逻辑不适用。
            if (!(t is Blueprint_Build) && !(t is Frame)) return false;
            return FactionMatches(pawn, t);
        }

        /// <summary>
        /// 目标必须属于**自己派系** —— 原版三道建造闸门都有这一条，逐字相同：
        /// <c>WorkGiver_ConstructDeliverResourcesToBlueprints.HasJobOnThing:12</c>、
        /// <c>...ToFrames.HasJobOnThing:12</c>、<c>WorkGiver_ConstructFinishFrames.JobOnThing:19</c>
        /// 全都是 <c>if (t.Faction != pawn.Faction) return …</c>。
        ///
        /// <para><b>为什么本适配器必须自己查</b>：本类的 <see cref="TrustWorkGiver"/> = false
        /// （原版闸门里含可达性判定，而代理建筑是隔空干活的）⇒ 上面那三行**一次都不会跑**。
        /// 而围攻 / 迫击炮袭击的 <c>SiegeBlueprintPlacer:126/147</c> 放下的沙袋与迫击炮蓝图
        /// 带的就是**袭击者派系**（那里传的正是 lord 的 faction）——
        /// 少这一条，代理会拿我们核心里的材料把敌方蓝图变成框、还替它把料填满。</para>
        ///
        /// <para>口径与原版完全一致（<c>Faction == null</c> 也算"不是我的"）：玩家自己放下的蓝图/框
        /// 一定是 <c>Faction.OfPlayer</c> —— <c>Designator_Build:522</c> 直接传 <c>Faction.OfPlayer</c>，
        /// 而蓝图转框时 <c>Blueprint.TryReplaceWithSolidThing:71-73</c> 会把 <c>workerPawn.Faction</c>
        /// 写进框里。殖民者的取料路径（<c>WorkGiver_DS_WithdrawForConstruct:42</c> /
        /// <c>DSConstructionDelivery.IsValidTarget:120</c>）用的也是同一个判据，两条路从此一致。</para>
        /// </summary>
        internal static bool FactionMatches(Pawn pawn, Thing t)
        {
            if (t == null) return false;
            if (pawn == null || pawn.Faction == null) return false;
            return t.Faction == pawn.Faction;
        }
    }

    public class DigitalTask_Construct : DigitalTask
    {
        public override string Label
        {
            get { return "DS_Task_Build".Translate().ToString(); }
        }

        private Frame FrameTarget
        {
            get { return target as Frame; }
        }

        public override bool StillValid(Pawn pawn, Map map)
        {
            if (target == null || target.Destroyed || !target.Spawned) return false;

            Blueprint bp = target as Blueprint;
            // 蓝图：等我们供料；核心凑不齐会自己放手（见 Work）。
            // ⚠️ 阵营同样要查 —— 围攻袭击的沙袋/迫击炮蓝图属于袭击者，
            // 放它过去就等于"用我们的材料替敌人转框+供料"（见 FactionMatches 的注释）。
            if (bp != null)
            {
                if (!DigitalTaskAdapter_Construct.FactionMatches(pawn, bp)) return false;
                // 挡路的**植物**照旧认领（Work 阶段 0 会把它割掉）；
                // 物品/建筑不认领 —— 原版会派人把它搬走 / 拆掉（HandleBlockingThingJob），
                // 我们不抢那种活，也就不会去碰 TryReplaceWithSolidThing 的 EndCurrentJob 分支。
                Thing blocker = GenConstruct.FirstBlockingThing(bp, pawn);
                if (blocker != null && !(blocker is Plant)) return false;
                return true;
            }

            Frame f = FrameTarget;
            if (f == null) return false;
            // 材料没齐 ⇒ 等阶段 1 供料。**不再要求 WorkLeft > 0**：
            // "活干完了但没结算"的框（旧版本预算不足留下的残留、或别的路径中途放手）
            // 也要继续认领 —— 阶段 2 会直接把它结算掉，等于自愈（见 Work 里的预算前置检查）。
            if (!f.IsCompleted()) return false;
            if (!DigitalTaskAdapter_Construct.FactionMatches(pawn, f)) return false;
            if (f.IsBurning()) return false;
            if (f.def.constructionSkillPrerequisite > 0 && pawn.skills != null
                && pawn.skills.GetSkill(SkillDefOf.Construction).Level < f.def.constructionSkillPrerequisite)
            {
                return false;
            }
            return GenConstruct.FirstBlockingThing(f, pawn) == null;
        }

        public override float Progress01
        {
            get
            {
                Frame f = FrameTarget;
                if (f == null || f.WorkToBuild <= 0f) return -1f;
                return Mathf.Clamp01(f.workDone / f.WorkToBuild);
            }
        }

        public override void Work(Pawn pawn, Map map, float speedMult)
        {
            if (target == null || target.Destroyed || !target.Spawned) return;

            // ---- 阶段 1：蓝图 → 框 + 从核心补料 ----
            Blueprint bp = target as Blueprint;
            if (bp != null)
            {
                // ---- 阶段 0：挡路的东西（原版先给"清障作业"：GenConstruct.HandleBlockingThingJob:767）----
                // 植物 ⇒ 原版派 CutPlant；我们是隔空干活的，直接照割除语义砍掉（收尾走与植物任务
                // 共用的 DigitalPlantWork.Harvest）。**绝不能**带着阻挡去调
                // Blueprint.TryReplaceWithSolidThing —— 它在有阻挡时会调
                // `workerPawn.jobs.EndCurrentJob(...)`（Blueprint.cs:51），而这是**假 pawn**：
                // 于是（a）日志爆红，（b）给它装上找活/寻路，留下一条指向它的 PathRequest，
                // 出作用域后 pawn.Map 变 null ⇒ PathFinder 报 "Tried to FindPath for pawn
                // which is spawned in another map"。物品/建筑交给原版（搬走 / 拆除）。
                Thing blocker = GenConstruct.FirstBlockingThing(bp, pawn);
                if (blocker is Plant blockerPlant && !blockerPlant.Destroyed && blockerPlant.Spawned)
                {
                    DigitalPlantWork.Harvest(pawn, blockerPlant, false);   // asHarvest:false = 割除
                    return;                                               // 下一 tick 再供料/转框
                }
                if (blocker != null)
                {
                    Abort = true;
                    return;
                }

                // 地形蓝图（地板 / 地基）铺在"已经有可拆上层"的格子上：原版交付层会**先**拆掉旧地板
                // （WorkGiver_ConstructDeliverResourcesToBlueprints.JobOnThing:89 → RemoveExistingFloorJob
                // → JobDriver_RemoveFloor → TerrainGrid.RemoveTopLayer），下一趟才送料。我们绕过了那一层
                // ⇒ 这里补上：本 tick 只清地，下一 tick 再供料；否则完成时 SetFoundation 会因为
                // "there is an under terrain there" 被拒，而框那时已经 Destroy（框 / 料 / 工全丢）。
                if (DSConstructionDelivery.RemoveExistingFloorFirstIfNeeded(bp, map)) return;

                if (!TrySupplyFromCores(pawn, map, bp))
                {
                    // 核心凑不齐 ⇒ 让路给原版搬运工（只放弃这一件，别动别的活）
                    Abort = true;
                }
                return;
            }

            // ---- 阶段 2：框 → 建完（照抄 JobDriver_ConstructFinishFrame.cs:44-80 的算式）----
            Frame f = FrameTarget;
            if (f == null || f.Destroyed) return;

            float num = speedMult * 1.7f;                 // 原版是 ConstructionSpeed * 1.7
            if (f.Stuff != null)
            {
                num *= f.Stuff.GetStatValueAbstract(StatDefOf.ConstructionSpeedFactor);
            }

            float workToBuild = f.WorkToBuild;
            if (workToBuild <= 0f)
            {
                if (!DigitalWorkBudget.AllowCompletion()) return;   // 超预算：下一 tick 再建完
                ForeignJobRelease.BeforeVanishing(f, pawn);
                f.CompleteConstruction(pawn);
                return;
            }

            // ⚠️ 这里**没有**原版那条"建造失败"掷骰（JobDriver_ConstructFinishFrame.cs:59-67：
            //    `Rand.Value < 1 - Pow(ConstructSuccessChance, 本次进度/总工时)` ⇒ Frame.FailConstruction，
            //    框里的材料白费、还要重新取料）。代理是机器："活干完了就该有产出"。
            //    全局口径（含殖民者）见 Settings.WorkFailureTuner。

            // ⚠️ 结算票必须在**加工作量之前**拿。先 `+= num` 再因为没票而不 CompleteConstruction，
            //    框会停在"活干完了但没结算"的状态，而 StillValid 只认 WorkLeft > 0
            //    ⇒ 这只手下一 tick 直接放手 ⇒ **框架永久残留**
            //    （用户实测：代理把地板建好了、框架还在 = 上一次超预算的那一格）。
            //    所以这一步会把活干完时：先拿票，拿不到就这一 tick 什么都不做，下一 tick 重试。
            bool willFinish = f.workDone + num >= workToBuild;
            if (willFinish && !DigitalWorkBudget.AllowCompletion()) return;

            f.workDone += num;
            StrikeCount++;
            if (willFinish)
            {
                ForeignJobRelease.BeforeVanishing(f, pawn);
                f.CompleteConstruction(pawn);
            }
        }

        public override bool Finished
        {
            get
            {
                if (Abort) return true;
                Frame f = FrameTarget;
                if (f != null && !f.Destroyed && f.IsCompleted() && f.WorkLeft <= 0f) return true;
                return base.Finished;
            }
        }

        /// <summary>
        /// 把蓝图变成框，并把核心里的料塞进 <c>frame.resourceContainer</c>。
        /// 返回 false = 核心凑不齐（调用方应放手让原版搬运工做）。
        /// </summary>
        private bool TrySupplyFromCores(Pawn pawn, Map map, Blueprint bp)
        {
            List<ThingDefCountClass> cost = bp.TotalMaterialCost();
            if (cost == null || cost.Count == 0)
            {
                // 0 成本蓝图（原版走 JobDefOf.PlaceNoCostFrame）：直接转框，不用供料
                return TryReplace(pawn, bp);
            }

            // 先数一遍，不够就整单不做（不做"半送"—— 半送的料会留在框里等下一次，语义更乱）
            for (int i = 0; i < cost.Count; i++)
            {
                ThingDefCountClass need = cost[i];
                if (need == null || need.thingDef == null || need.count <= 0) continue;
                if (CountInCores(map, need.thingDef) < need.count) return false;
            }

            if (!TryReplace(pawn, bp)) return false;

            // 转框后 target 已是 Frame；料塞进它的 resourceContainer
            Frame f = FrameTarget;
            if (f == null || f.Destroyed) return true;

            ThingOwner dest = f.TryGetInnerInteractableThingOwner();
            if (dest == null) return true;

            for (int i = 0; i < cost.Count; i++)
            {
                ThingDefCountClass need = cost[i];
                if (need == null || need.thingDef == null || need.count <= 0) continue;
                // ★ 只补"还差的"，不是"总成本"：框的内胆是 maxStacks=999999 的 ThingOwner，
                //   什么都能收；多塞进去的料会在 `Frame.CompleteConstruction` 里被
                //   `ClearAndDestroyContents()` 直接销毁（`RimWorld\Frame.cs:277`）⇒ 材料凭空消失。
                //   还要扣掉原版搬运工/其它小人在途的量（IHaulEnroute），否则多人同时供料必超发。
                int stillNeeded = need.count;
                try
                {
                    stillNeeded = (f is IHaulEnroute enroute)
                        ? enroute.GetSpaceRemainingWithEnroute(need.thingDef)
                        : f.ThingCountNeeded(need.thingDef);
                }
                catch (Exception)
                {
                    // 第三方 IHaulEnroute 实现可能抛；算不出来就退回"总成本"（旧行为）
                    stillNeeded = need.count;
                }
                if (stillNeeded <= 0) continue;
                TransferFromCores(map, need.thingDef, stillNeeded, dest);
            }
            return true;
        }

        private bool TryReplace(Pawn pawn, Blueprint bp)
        {
            Thing created;
            bool jobEnded;
            // 换框 = 蓝图消失。殖民者手上那条"往蓝图送货/建造"的 job 会因此指着空气，
            // 必须同一 tick 收掉（否则那条 job 一旦落进存档，读档时 targetA 静默变 Invalid
            // ⇒ 原版 JobDriver_ConstructFinishFrame 解引用 null 炸档，见 ForeignJobRelease 注释）。
            // 换框失败（第三方拦了）时白收一次也无害：它下一 tick 会重新领同一个活。
            ForeignJobRelease.BeforeVanishing(bp, pawn);
            if (!bp.TryReplaceWithSolidThing(pawn, out created, out jobEnded))
            {
                return false;
            }
            if (created != null)
            {
                target = created;   // 之后就是 Frame
            }
            return true;
        }

        private static int CountInCores(Map map, ThingDef def)
        {
            int total = 0;
            List<IHaulSource> cores = HaulSourceContents.CoreSources(map);
            for (int i = 0; i < cores.Count; i++)
            {
                ThingOwner owner = cores[i].GetDirectlyHeldThings();
                if (owner == null) continue;
                for (int j = 0; j < owner.Count; j++)
                {
                    Thing t = owner[j];
                    if (t != null && !t.Destroyed && t.def == def) total += t.stackCount;
                }
            }
            return total;
        }

        private static void TransferFromCores(Map map, ThingDef def, int count, ThingOwner dest)
        {
            int left = count;
            List<IHaulSource> cores = HaulSourceContents.CoreSources(map);
            for (int i = 0; i < cores.Count && left > 0; i++)
            {
                ThingOwner owner = cores[i].GetDirectlyHeldThings();
                if (owner == null) continue;
                for (int j = owner.Count - 1; j >= 0 && left > 0; j--)
                {
                    Thing t = owner[j];
                    if (t == null || t.Destroyed || t.def != def) continue;
                    int before = t.stackCount;
                    owner.TryTransferToContainer(t, dest, Mathf.Min(left, before), true);
                    // ⚠️ 不能信 `TryTransferToContainer` 的返回值：原版在"整堆并进目标已有堆"时
                    // 走合并分支、`thing.stackCount` 不变 ⇒ 返回 0（`Verse\ThingOwner.cs:750-767`）。
                    // 4.0 之前就因此少减 `left`，虚拟工实际搬得比需求多。这里按转移前后的真实数量记账。
                    int moved;
                    if (t.Destroyed || t.holdingOwner != owner) moved = before;   // 整堆转走 / 被并掉
                    else moved = before - t.stackCount;
                    if (moved > 0) left -= moved;
                }
            }
        }
    }

    // ==========================================================================================
    // 清洁 —— 照抄 JobDriver_CleanFilth 的算式；**放宽原版"只扫 Home 区"**（用户拍板）
    // ==========================================================================================
    public class DigitalTaskAdapter_Clean : DigitalTaskAdapter
    {
        /// <summary>只认 Cleaning（见 <see cref="DigitalTaskAdapter.HomeWorkTypes"/>）。</summary>
        private static readonly string[] Homes = { "Cleaning" };

        public override Type WorkGiverClass
        {
            get { return typeof(WorkGiver_CleanFilth); }
        }

        public override string[] HomeWorkTypes
        {
            get { return Homes; }
        }

        /// <summary>
        /// 不信任原版：<c>WorkGiver_CleanFilth</c> 的候选集来自 <c>listerFilthInHomeArea</c>、
        /// 闸门里也卡 Home 区。用户拍板"放宽 Home 区（全图污物都能清）"⇒ 候选集换成全图 Filth，
        /// 迷雾/厚度那几道自己查。
        /// </summary>
        public override bool TrustWorkGiver
        {
            get { return false; }
        }

        public override IEnumerable<Thing> CandidateSet(Map map, Pawn pawn, WorkGiver_Scanner scanner)
        {
            // ⚠️ **不能**用 `map.listerThings.ThingsInGroup(ThingRequestGroup.Filth)` ——
            // `ThingListGroupHelper`（ListerThings 的注册判据）里**没有 Filth 这一组**，
            // 那个列表**永远是空的**（实测：清洁代理解析不到任何污物、一件活都不干）。
            // 原版 WorkGiver_CleanFilth 走的是专用 lister `Map.listerFilthInHomeArea`（只含家区），
            // 而我们拍板"放宽 Home 区"，所以这里退到 AllThings 过滤（全图污物）。
            //
            // ⚠️ 但**不能在这里当场扫全图**：几十万 item 的全图扫描是一次 3~15ms 的尖峰，
            // 而 CandidateSet 一次扫描里最多被调 60 次（AddsPerScan）⇒ 实测 DS-scan 40~59ms/帧。
            // 现在改成"分帧快照"：由 SliceTick 每 tick 按索引推进一小片，这里只返回当前快照。
            FilthSnapshot snap = SnapshotFor(map);
            return snap == null ? (IEnumerable<Thing>)null : snap.Current;
        }

        public override void SliceTick(Map map, Pawn pawn, WorkGiver_Scanner scanner)
        {
            FilthSnapshot snap = SnapshotFor(map);
            if (snap != null) snap.Advance(map);
        }

        private static FilthSnapshot SnapshotFor(Map map)
        {
            if (map == null) return null;
            FilthSnapshot snap;
            if (snapshots.TryGetValue(map, out snap)) return snap;
            PruneSnapshots();
            snap = new FilthSnapshot();
            snapshots[map] = snap;
            return snap;
        }

        /// <summary>图被销毁后丢掉快照，别让静态表无限涨。</summary>
        private static void PruneSnapshots()
        {
            if (snapshots.Count < 8) return;
            List<Map> dead = null;
            foreach (KeyValuePair<Map, FilthSnapshot> kv in snapshots)
            {
                if (kv.Key != null && Find.Maps != null && Find.Maps.Contains(kv.Key)) continue;
                if (dead == null) dead = new List<Map>();
                dead.Add(kv.Key);
            }
            if (dead == null) return;
            for (int i = 0; i < dead.Count; i++) snapshots.Remove(dead[i]);
        }

        /// <summary>每 tick 最多走多少个 AllThings 条目（分帧片大小）。</summary>
        private const int SlicePerTick = 3000;

        private static readonly Dictionary<Map, FilthSnapshot> snapshots = new Dictionary<Map, FilthSnapshot>();

        /// <summary>
        /// 全图污物快照（<b>双缓冲 + 按索引分帧推进</b>）。
        ///
        /// <list type="bullet">
        /// <item><b>按索引而不是枚举器</b>：切片要跨 tick，而 <c>AllThings</c> 会被其它 agent
        /// （原版搬运工、我们自己的完成逻辑）增删 —— 跨 tick 持有枚举器必抛
        /// <c>InvalidOperationException</c>。索引遍历对增删安全（至多漏看/重看一格）。</item>
        /// <item><b>双缓冲</b>：新一轮先进 <c>pending</c>，走完才与 <c>current</c> 互换
        /// ⇒ 任何一帧都有可用的（略旧的）候选集，不会出现"重建期间一件活都找不到"。</item>
        /// <item>一 tick 只推进一片（<see cref="SlicePerTick"/>）：几十万 item ≈ 每 tick 0.05ms，
        /// 一轮约 1 秒走完，旧快照最久也就是这个年龄。</item>
        /// </list>
        /// </summary>
        private sealed class FilthSnapshot
        {
            private List<Thing> current = new List<Thing>();
            private List<Thing> pending = new List<Thing>();
            private int cursor;
            private int lastSliceTick = int.MinValue;

            public List<Thing> Current
            {
                get { return current; }
            }

            public void Advance(Map map)
            {
                int now = GenTicks.TicksGame;
                if (now == lastSliceTick) return;      // 一 tick 只推一片（被调 60 次也一样）
                lastSliceTick = now;

                List<Thing> all = map.listerThings.AllThings;
                if (all == null) return;

                long t0 = Performance.DevDrawProfiler.Stamp();
                int seen = 0;
                while (cursor < all.Count && seen < SlicePerTick)
                {
                    Thing t = all[cursor];
                    cursor++;
                    seen++;
                    if (t is Filth) pending.Add(t);
                }
                Performance.DevDrawProfiler.Mark("ScanSet", t0);
                Performance.DevDrawProfiler.Bump("清切片", seen);

                if (cursor >= all.Count)
                {
                    List<Thing> tmp = current;
                    current = pending;
                    pending = tmp;
                    pending.Clear();
                    cursor = 0;
                    Performance.DevDrawProfiler.Bump("清池换", 1);
                }
            }
        }

        public override DigitalTask MakeTask(Thing t, CompDigitalWorker comp)
        {
            return new DigitalTask_Clean { target = t, comp = comp };
        }

        public override bool CanTarget(Pawn pawn, Thing t)
        {
            return t is Filth;
        }
    }

    public class DigitalTask_Clean : DigitalTask
    {
        private float cleaningWorkDone;
        private float totalWorkRequired;

        public override string Label
        {
            get { return "DS_Task_Clean".Translate().ToString(); }
        }

        private Filth FilthTarget
        {
            get { return target as Filth; }
        }

        public override bool StillValid(Pawn pawn, Map map)
        {
            Filth filth = FilthTarget;
            if (filth == null || filth.Destroyed || !filth.Spawned) return false;
            if (map.fogGrid != null && map.fogGrid.IsFogged(filth.Position)) return false;
            // 原版 WorkGiver_CleanFilth 的节流：污物刚出现 600 tick 内不清理
            return filth.TicksSinceThickened >= 600;
        }

        public override float Progress01
        {
            get
            {
                Filth filth = FilthTarget;
                if (filth == null || filth.Destroyed || totalWorkRequired <= 0f) return -1f;
                float remaining = filth.thickness * filth.def.filth.cleaningWorkToReduceThickness - cleaningWorkDone;
                return Mathf.Clamp01(1f - remaining / totalWorkRequired);
            }
        }

        public override void Work(Pawn pawn, Map map, float speedMult)
        {
            Filth filth = FilthTarget;
            if (filth == null || filth.Destroyed) return;

            if (totalWorkRequired <= 0f)
            {
                totalWorkRequired = filth.def.filth.cleaningWorkToReduceThickness * filth.thickness;
            }

            float terrainFactor = filth.Position.GetTerrain(filth.Map)
                .GetStatValueAbstract(StatDefOf.CleaningTimeFactor);
            float num = speedMult;      // 速度只认倍率（原版 CleaningSpeed 本来就不吃技能/能力）
            if (terrainFactor != 0f)
            {
                num /= terrainFactor;
            }

            cleaningWorkDone += num;
            if (cleaningWorkDone > filth.def.filth.cleaningWorkToReduceThickness)
            {
                if (!DigitalWorkBudget.AllowCompletion()) return;   // 超预算：下一 tick 再削
                filth.ThinFilth();
                StrikeCount++;
                cleaningWorkDone = 0f;
                if (filth.Destroyed && pawn.records != null)
                {
                    pawn.records.Increment(RecordDefOf.MessesCleaned);
                }
            }
        }
    }

    // ==========================================================================================
    // 伐木 / 割除 / 收获标记 —— 照抄 JobDriver_PlantWork 的产出段
    //
    // 归属：用户把"伐木"归进种植（每类一个专用建筑），所以种植代理的 workTypes = Growing + PlantCutting。
    // 本适配器挂的是 **PlantCutting** 下的 WorkGiver_PlantsCut —— 注意它同时枚举
    // `CutPlant` 与 `HarvestPlant` 两枚标记（WorkGiver_PlantsCut.PotentialWorkThingsGlobal），
    // 所以"右键收获"这条路一直是通的；而 Growing 下的**自动收割**与**播种**是另一条
    // scanCells 型管线，见 DigitalPlantTasks.cs。
    // 产出段已抽到 DigitalPlantWork.Harvest（与自动收割共用）。
    // ==========================================================================================
    public class DigitalTaskAdapter_PlantCut : DigitalTaskAdapter
    {
        /// <summary>只认 PlantCutting（见 <see cref="DigitalTaskAdapter.HomeWorkTypes"/>）。</summary>
        private static readonly string[] Homes = { "PlantCutting" };

        public override Type WorkGiverClass
        {
            get { return typeof(WorkGiver_PlantsCut); }
        }

        public override string[] HomeWorkTypes
        {
            get { return Homes; }
        }

        public override DigitalTask MakeTask(Thing t, CompDigitalWorker comp)
        {
            return new DigitalTask_PlantCut { target = t, comp = comp };
        }

        public override bool CanTarget(Pawn pawn, Thing t)
        {
            return t is Plant;
        }
    }

    public class DigitalTask_PlantCut : DigitalTask
    {
        private float workDone;

        /// <summary>
        /// 这一件是"收获/伐木"（designation = <c>HarvestPlant</c>）还是"割除"（<c>CutPlant</c>）。
        /// 两者在 <see cref="StillValid"/> 里判出来，决定收尾用哪种 <c>PlantDestructionMode</c> 与清谁的标记。
        /// </summary>
        private bool asHarvest;

        public override string Label
        {
            get { return asHarvest ? "DS_Task_CutHarvest".Translate().ToString() : "DS_Task_Cut".Translate().ToString(); }
        }

        private Plant PlantTarget
        {
            get { return target as Plant; }
        }

        public override bool StillValid(Pawn pawn, Map map)
        {
            Plant p = PlantTarget;
            if (p == null || p.Destroyed || !p.Spawned) return false;

            // ⚠️ 三个设计器只打两种标记（RimWorld\Designator_Plants*.cs）：
            //    Designator_PlantsCut        → DesignationDefOf.CutPlant     （"割除"）
            //    Designator_PlantsHarvest    → DesignationDefOf.HarvestPlant （"收获"）
            //    Designator_PlantsHarvestWood→ DesignationDefOf.HarvestPlant （"伐木"！同一枚标记）
            // 所以两种都要认，而且收尾方式不同（见 Harvest）。
            if (map.designationManager.DesignationOn(p, DesignationDefOf.HarvestPlant) != null)
            {
                asHarvest = true;
                return true;
            }
            if (map.designationManager.DesignationOn(p, DesignationDefOf.CutPlant) != null)
            {
                asHarvest = false;
                return true;
            }
            return false;
        }

        public override float Progress01
        {
            get
            {
                Plant p = PlantTarget;
                if (p == null || p.def.plant.harvestWork <= 0f) return -1f;
                return Mathf.Clamp01(workDone / p.def.plant.harvestWork);
            }
        }

        public override void Work(Pawn pawn, Map map, float speedMult)
        {
            Plant p = PlantTarget;
            if (p == null || p.Destroyed) return;

            // 原版 JobDriver_PlantWork.WorkDonePerTick = PlantWorkSpeed * Lerp(3.3, 1, Growth)
            // 这里把 PlantWorkSpeed 换成建筑倍率（速度只认倍率），Growth 那一项照抄。
            workDone += DigitalPlantWork.WorkPerTick(speedMult, p);
            StrikeCount++;

            if (workDone < p.def.plant.harvestWork) return;
            if (!DigitalWorkBudget.AllowCompletion()) return;   // 超预算：下一 tick 再收

            DigitalPlantWork.Harvest(pawn, p, asHarvest);
            workDone = 0f;
        }
    }

    // ==========================================================================================
    // 拆除 —— 照抄 JobDriver_Deconstruct（More Organs LaborTask_Deconstruct 的移植）
    //
    // 归属：拆除在游戏里挂在 **Construction** 工作类型下（WorkGiver_Deconstruct, priorityInType 50），
    // 所以"建造代理"应当同时会建与拆。早期版本每个工作类型只取"第一个有适配器的 WorkGiver"，
    // 于是建造代理永远不拆建筑 —— 现在 FindGivers 会返回该工作类型下所有有适配器的 giver。
    // ==========================================================================================
    public class DigitalTaskAdapter_Deconstruct : DigitalTaskAdapter
    {
        /// <summary>Construction —— 外加 Complex Jobs 把 <c>Deconstruct</c> 搬去的 <c>FSFDeconstruct</c>
        /// （见 <see cref="DigitalTaskAdapter.HomeWorkTypes"/>；不装那个 mod 时这个 defName 不存在）。</summary>
        private static readonly string[] Homes = { "Construction", "FSFDeconstruct" };

        public override Type WorkGiverClass
        {
            get { return typeof(WorkGiver_Deconstruct); }
        }

        public override string[] HomeWorkTypes
        {
            get { return Homes; }
        }

        public override DigitalTask MakeTask(Thing t, CompDigitalWorker comp)
        {
            return new DigitalTask_Deconstruct { target = t, comp = comp };
        }

        public override bool CanTarget(Pawn pawn, Thing t)
        {
            return t is Building || t is MinifiedThing;
        }
    }

    public class DigitalTask_Deconstruct : DigitalTask
    {
        private const float MaxDeconstructWork = 3000f;
        private const float MinDeconstructWork = 20f;

        private float workLeft;
        private float totalNeededWork;

        public override string Label
        {
            get { return "DS_Task_Deconstruct".Translate().ToString(); }
        }

        private Building BuildingTarget
        {
            get { return (target == null) ? null : (target.GetInnerIfMinified() as Building); }
        }

        public override bool StillValid(Pawn pawn, Map map)
        {
            // ⚠️ 判的是**外层**（拆除命令挂在外层）在图上，不是"内层建筑 spawned"：
            //    迷你化建筑（<c>MinifiedThing</c>）里的内层建筑**永远不 spawn**（被外层 ThingOwner 持有），
            //    旧写法 `!b.Spawned` 会让 CanTarget 放行的迷你建筑**永远拿不到拆除活**（静默跳过）。
            //    原版拆的也是外层（JobDriver_Deconstruct 的 Target 就是那个 MinifiedThing）。
            if (target == null || target.Destroyed || !target.Spawned) return false;

            Building b = BuildingTarget;
            if (b == null || b.Destroyed) return false;
            if (pawn.Faction != null && !b.DeconstructibleBy(pawn.Faction)) return false;
            if (map.designationManager.DesignationOn(target, DesignationDefOf.Deconstruct) == null) return false;
            CompExplosive explosive = b.TryGetComp<CompExplosive>();
            return explosive == null || !explosive.wickStarted;
        }

        public override float Progress01
        {
            get { return totalNeededWork <= 0f ? -1f : Mathf.Clamp01(1f - workLeft / totalNeededWork); }
        }

        public override void Work(Pawn pawn, Map map, float speedMult)
        {
            Building b = BuildingTarget;
            if (b == null || b.Destroyed) return;

            if (totalNeededWork <= 0f)
            {
                // 原版 JobDriver_Deconstruct：工时 = Clamp(WorkToBuild, 20, 3000)，速度 = ConstructionSpeed * 1.7
                totalNeededWork = Mathf.Clamp(b.GetStatValue(StatDefOf.WorkToBuild), MinDeconstructWork, MaxDeconstructWork);
                workLeft = totalNeededWork;
            }

            workLeft -= speedMult * 1.7f;
            StrikeCount++;
            if (workLeft > 0f) return;
            if (!DigitalWorkBudget.AllowCompletion()) return;   // 超预算：下一 tick 再拆

            Thing t = target;
            if (t.Faction != null)
            {
                t.Faction.Notify_BuildingRemoved(b, pawn);
            }
            // 同一 tick 收掉"正指着这栋建筑"的殖民者 job（见 ForeignJobRelease 注释：
            // 拆完的建筑若仍挂在某人的 curJob.targetA 上，那次存档会写出一条读档必炸的 job）。
            ForeignJobRelease.BeforeVanishing(t, pawn);
            t.Destroy(DestroyMode.Deconstruct);
            if (pawn.records != null)
            {
                pawn.records.Increment(RecordDefOf.ThingsDeconstructed);
            }
            map.designationManager.RemoveAllDesignationsOn(t);
        }
    }
}
