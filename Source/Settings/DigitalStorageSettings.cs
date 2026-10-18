using System.Globalization;
using UnityEngine;
using Verse;

namespace DigitalStorage.Settings
{
    /// <summary>
    /// Mod 设置：三个<b>数值倍率</b>（造价 / 电力 / 研究点数）、日志开关、以及社区反馈要求的访问/收纳开关。
    ///
    /// <para>倍率都是**全局**的（含原版与其它 mod），施加逻辑见 <see cref="DefMultipliers"/> ——
    /// 这里只负责改值 + 立刻施加。滑条是<b>离散档位</b>（<see cref="MultiplierLadder"/>）：
    /// 1/100 ~ 100× 跨了四个数量级，线性滑条会让"1×"挤在 1% 的位置上没法用，
    /// 所以档位按等比铺开，两端都点得到。</para>
    /// </summary>
    public class DigitalStorageSettings : ModSettings
    {
        /// <summary>造价倍率：所有建筑 / 地板 / 物品的建造材料。</summary>
        public static float costMultiplier = 1.0f;

        /// <summary>电力倍率：所有建筑的耗电量（发电不受影响）。</summary>
        public static float powerMultiplier = 1.0f;

        /// <summary>研究点数倍率：所有研究的所需点数。</summary>
        public static float researchMultiplier = 1.0f;

        public static bool enableDebugLog = false;

        /// <summary>
        /// 自动收纳：核心每 15 tick 把附近散落物品吸入自身**容器**。
        /// （3.0 是吸入账本；4.0 改投真实容器。）
        /// 关掉后仍可用原版搬运工单，只是没有「隔空收纳」。
        /// （社区反馈：「禁止自动收纳后（瞬移搬运）就没有出现了」→ 给玩家开关）
        /// </summary>
        public static bool autoIngestEnabled = true;

        /// <summary>
        /// <b>自动收纳是否"避让正在用这件东西的人"</b>（作业目标 / 还有 bill 在等料的工作台旁边那一圈）。
        ///
        /// <para><b>2026-10-18 起默认开</b>（社区反馈第 3 条：「小人会无限从核心搬运食物原材料、
        /// 核心无限存食物，循环导致非常卡」）：这两条闸门当初（commit <c>e66978d</c>）就是为
        /// 「小人取出材料准备制造 → 材料立刻被吸走 → 反复取物卡住」那个死循环加的；
        /// 因为老玩家存档里 Scribe 已经写了 <c>false</c>，默认值改成 true **只影响新档**
        /// （老档保持原值，玩家可在设置里一键切换）。</para>
        ///
        /// <para>打开 = 让位：作业目标一直让到作业结束；有 bill 在等料的工作台旁边让 300 tick。
        /// 关闭 = 算中即入：候选一进原版待搬表，15 tick 内就瞬移进核心（本 mod 最初的取向）。</para>
        ///
        /// <para>配合 <c>Patch_CarryFromCore_MarkWithdrawn</c>：原版取料路径拿走的物品也会被盖上
        /// "刚被取出"戳（300 tick），落地后不会被立刻吸回。</para>
        /// </summary>
        public static bool autoIngestYieldToJobs = true;

        /// <summary>
        /// <b>制作代理是否识别"mod 工作台"</b>（社区反馈 2026-10-18 第 2 条）。
        ///
        /// <para>面板能选的配方来自"范围内工作台能做什么"（<c>ThingDef.AllRecipes</c>），
        /// 但轮到代理去取活时，调的是原版 <c>WorkGiver_DoBill.JobOnThing</c> —— 它第一道门
        /// <c>ThingIsUsableBillGiver</c> 只认 <c>WorkGiverDef.fixedBillGiverDefs</c> 里列出的台子
        /// （原版 19 个 DoBill giver 里有 16 个带精确 defName 白名单：DoBillsCook 只认电炉/燃料炉…）。
        /// 于是 mod 工作台**菜单里能选、选了永远不干活**，面板只显示"没有可做的配方"。</para>
        ///
        /// <para>打开（默认）= 取活时对"没有任何 giver 认领的台子"放宽这道白名单
        /// （补丁 <c>Patch_BenchWhitelist_Relax</c>，只在代理取活的作用域内生效，
        /// 真小人的行为一点不变）。关掉 = 完全退回原版口径（只有原版台子能自动化）。
        /// 上游 <c>Digital-Storage-Extension-Patch</c> 同一修复叫 <c>craftAnyBench</c>。</para>
        /// </summary>
        public static bool craftAnyBench = true;

        /// <summary>
        /// <b>制作代理的扫描半径</b>（格；方形，边长 = 2r+1）。默认 6 ⇒ 13×13。
        ///
        /// <para>老实现把这个值写死在 <c>CompProperties_BillAutomation.scanRadius</c>（XML）里，
        /// 玩家只能靠"把代理建在正中"来迁就；工作间一大就"只认得出眼前三张台子"。
        /// 收进设置后可以在游戏里直接改（下一次扫描生效，60 tick 内）。</para>
        ///
        /// <para>上限 20（41×41）：再大就不是"就近生产"而是全图调度了，扫描代价也线性涨。</para>
        /// </summary>
        public static int craftScanRadius = 6;

        /// <summary>
        /// <b>配方展开深度</b>（制作代理 · 用户拍板方案 A，2026-10-18）。
        ///
        /// <para>原版漏斗只认"配方原料本身在核心里"。缺<b>中间产物</b>（高级零部件这类）时，
        /// <see cref="AI.RecipeExpansion"/> 会把它摊成"做它的那份原料"，一步造出最终产物。
        /// 这个值就是"能连着摊几层"：</para>
        /// <list type="bullet">
        /// <item><b>1</b> —— 只把最终配方的直接原料里的中间件摊开一层
        ///   （超凡仿生眼 ← 高级零部件？摊成 精密零部件 + 钢）。</item>
        /// <item><b>2（默认）</b> —— 摊出来的东西若本身也是中间件，再摊一层
        ///   （覆盖 超凡仿生眼 ← 高级零部件 ← 精密零部件 这种三级链）。</item>
        /// <item><b>3</b> —— 再深一层。再深就没意义了：显式上限防 mod 里超长链把一次取活拖成全 Def 遍历。</item>
        /// </list>
        /// <para>超过上限 ⇒ 放弃展开，退回原来的"缺料"状态（不会凭空变出材料）。</para>
        /// </summary>
        public static int craftExpandDepth = 2;

        /// <summary>
        /// <b>中间件工作量折扣</b>（0..1，默认 0.5 = 半额）：展开时把每个中间件的配方工作量
        /// 按这个比例计入总工作量（用户原文："中间件工作量按半额计入"）。
        ///
        /// <para>为什么不是 1.0：中间件并没有真的被造出来、也不占第二张台子，
        /// 全额会把"一步成型"变成单纯的慢；0 又等于白拿。0.5 是可调的折中。</para>
        /// </summary>
        public static float craftExpandWorkFactor = 0.5f;

        /// <summary>
        /// <b>多个小人能不能同时往同一个核心送货</b>（社区反馈 2026-10-18 第 4 条）。
        ///
        /// <para>原版 <c>JobDriver_HaulToContainer.TryMakePreToilReservations</c> 对容器
        /// （<c>targetB</c>）发的是**单名额预约**：同一时刻只允许一个搬运工往同一个容器里放东西。
        /// 核心是个 3×3 的大仓库，却因此和"往工作台塞一份料"共用同一条规则 ——
        /// 远一点的物品就只有一个小人在搬，其他人站在"目标已被预订"外面排队。</para>
        ///
        /// <para>打开（默认）= 只对**本 mod 的核心**跳过容器的预约（补丁
        /// <c>Patch_HaulToContainer_ConcurrentCore</c>；货物本身与目标队列照旧预约，入库靠
        /// <c>TryTransferToContainer</c> 的原子性）。别家容器与原版装载舱完全按原版走。
        /// 关掉 = 退回原版串行入库。</para>
        /// </summary>
        public static bool concurrentCoreDelivery = true;

        /// <summary>
        /// 是否把「数字存储背包」也挂给**殖民地机械族**（含别的 mod 的无人机 / 机器人）。
        ///
        /// <para>背包的唯一用途是让"从核心取料"这一跳变成 0 距离（机械族也会做 bill），
        /// 所以默认开。但它是挂在**别人的 pawn** 上的一个 hediff —— 2026-10-03 有社区反馈
        /// 怀疑某个机械族 mod 的 Tick/绘制异常与本 mod 有关，所以留这个开关让玩家一键对照：
        /// 关掉后我们不再往机械族身上挂任何东西，其余功能（殖民者的背包）不受影响。</para>
        /// </summary>
        public static bool backpackForMechanoids = true;

        /// <summary>
        /// 原版热点的顺手优化，三块（详见 <c>Source/Performance/</c>）：
        /// ① 框选一堆东西时括号的视野裁剪；② 标记批绘矩阵的增量维护（原版每帧全量重建）；
        /// ③ 大批量标记时的"元气泡"每 tick 节流。
        /// <para>默认开。万一出现任何视觉异常，关掉即可**逐条退回原版行为**（不涉及存档）。</para>
        /// </summary>
        public static bool perfOptimizationsEnabled = true;

        /// <summary>
        /// <b>工作不会失败</b>：把原版三处"干完才发现白干"的掷骰（收割 / 建造 / 修故障）变成必成。
        /// 实际施加在 <see cref="WorkFailureTuner"/>（夹 stat 的因子，不动任何补丁）。
        ///
        /// <para>默认开 —— 本 mod 的取向是"代理建筑把活干完就该有产出"，
        /// 而这三处失败都是同一档体验：进度条走完、材料/作物已经消耗，最后给你一个红字。
        /// 关掉完全恢复原版（低技能小人照旧收获失败 / 建造失败）。</para>
        /// </summary>
        public static bool noWorkFailure = true;

        /// <summary>
        /// <b>每 tick 最多让代理"真正落地"几件活</b>（0 = 无限制）。
        ///
        /// <para>落地 = 走原版那一次性代价（挖完生成掉落物 / 建完生成建筑 / 拆完还材料 / 收获生成作物），
        /// 实测约 0.4~0.5ms/件。代理吞吐是"并行 75 × 7.5 倍速 ⇒ 每 tick 完成 20~40 件"，
        /// 也就是每 tick 要 10~20ms，而 60Hz 下每帧只有 16.7ms（且每帧必跑 1 tick）
        /// ⇒ 帧时间 20~56ms。压住这个数字，帧时间就变成常量；代价是扫图变慢。</para>
        ///
        /// <para>默认 16：约 960 件/秒（8000 格的图约 8~9 秒扫完），CPU 约 6~8ms/tick。</para>
        /// </summary>
        public static int workerCompletionsPerTick = 16;

        /// <summary>
        /// <b>每帧最多创建多少个 fleck（气泡特效）</b>（0 = 无限制）。
        ///
        /// <para>实测症状：250×250 全图标记一次 ⇒ <c>DesignationManager.AddDesignation</c>
        /// 给每个标记喷 4~6 个元气泡 ⇒ 几万个 fleck 同一瞬间创建、各自活 1~2 秒并逐帧绘制
        /// ⇒ "标记完闪一下、卡几秒"（实测 Flecks 吃 2.9~3.1ms/帧、峰值 29ms）。
        /// fleck 是纯视觉对象，丢掉只影响观感。</para>
        ///
        /// <para>默认 500/帧（≈3 万/秒）：正常游玩远低于此，无感；只有病态洪泛会被削平。</para>
        /// </summary>
        public static int fleckBudgetPerFrame = 500;

        /// <summary>
        /// 倍率档位：1/100 到 100× 等比铺开 18 档，两端都点得到，中间全是round数。
        /// 顺序必须递增（滑条按下标取值）。
        /// </summary>
        private static readonly float[] MultiplierLadder =
        {
            0.01f, 0.02f, 0.05f, 0.1f, 0.15f, 0.2f, 0.3f, 0.5f, 0.75f,
            1f, 1.5f, 2f, 3f, 5f, 10f, 20f, 50f, 100f
        };

        public override void ExposeData()
        {
            Scribe_Values.Look(ref costMultiplier, "costMultiplier", 1.0f);
            Scribe_Values.Look(ref powerMultiplier, "powerMultiplier", 1.0f);
            Scribe_Values.Look(ref researchMultiplier, "researchMultiplier", 1.0f);
            Scribe_Values.Look(ref enableDebugLog, "enableDebugLog", false);
            Scribe_Values.Look(ref autoIngestEnabled, "autoIngestEnabled", true);
            Scribe_Values.Look(ref autoIngestYieldToJobs, "autoIngestYieldToJobs", true);
            Scribe_Values.Look(ref craftAnyBench, "craftAnyBench", true);
            Scribe_Values.Look(ref craftScanRadius, "craftScanRadius", 6);
            Scribe_Values.Look(ref craftExpandDepth, "craftExpandDepth", 2);
            Scribe_Values.Look(ref craftExpandWorkFactor, "craftExpandWorkFactor", 0.5f);
            Scribe_Values.Look(ref concurrentCoreDelivery, "concurrentCoreDelivery", true);
            Scribe_Values.Look(ref backpackForMechanoids, "backpackForMechanoids", true);
            Scribe_Values.Look(ref perfOptimizationsEnabled, "perfOptimizationsEnabled", true);
            Scribe_Values.Look(ref noWorkFailure, "noWorkFailure", true);
            Scribe_Values.Look(ref workerCompletionsPerTick, "workerCompletionsPerTick", 16);
            Scribe_Values.Look(ref fleckBudgetPerFrame, "fleckBudgetPerFrame", 500);
            base.ExposeData();

            // 不在这里施加：读设置文件可能早于 XML 解析（DefDatabase 还空着）。
            // 施加由 [StaticConstructorOnStartup]（DefMultipliers 静态构造）与下面的滑条负责。
        }

        public static void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            bool changed = false;

            Text.Font = GameFont.Medium;
            listing.Label("DS_CostSettings".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            listing.Label("DS_CostMultiplier".Translate(FormatMultiplier(costMultiplier)));
            costMultiplier = MultiplierSlider(listing, costMultiplier, ref changed);
            listing.Gap(6f);
            listing.Label("DS_CostMultiplierDesc".Translate());
            listing.Gap(18f);

            listing.Label("DS_PowerMultiplier".Translate(FormatMultiplier(powerMultiplier)));
            powerMultiplier = MultiplierSlider(listing, powerMultiplier, ref changed);
            listing.Gap(6f);
            listing.Label("DS_PowerMultiplierDesc".Translate());
            listing.Gap(18f);

            listing.Label("DS_ResearchMultiplier".Translate(FormatMultiplier(researchMultiplier)));
            researchMultiplier = MultiplierSlider(listing, researchMultiplier, ref changed);
            listing.Gap(6f);
            listing.Label("DS_ResearchMultiplierDesc".Translate());
            listing.Gap(24f);

            Text.Font = GameFont.Medium;
            listing.Label("DS_AccessSettings".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            listing.CheckboxLabeled("DS_AutoIngestToggle".Translate(), ref autoIngestEnabled,
                "DS_AutoIngestToggleDesc".Translate());
            listing.Gap(6f);
            // 默认关 = 算中即入；开 = 让位给"正在用这件东西的人"（见字段注释）
            listing.CheckboxLabeled("DS_AutoIngestYieldToggle".Translate(), ref autoIngestYieldToJobs,
                "DS_AutoIngestYieldToggleDesc".Translate());
            listing.Gap(6f);
            listing.CheckboxLabeled("DS_BackpackMechToggle".Translate(), ref backpackForMechanoids,
                "DS_BackpackMechToggleDesc".Translate());
            listing.Gap(6f);
            // 社区反馈第 4 条：核心是 3×3 大仓库，不该像"往工作台塞一份料"那样单名额串行
            listing.CheckboxLabeled("DS_ConcurrentDeliveryToggle".Translate(), ref concurrentCoreDelivery,
                "DS_ConcurrentDeliveryToggleDesc".Translate());
            listing.Gap(6f);

            // 制作代理（社区反馈 2026-10-18 第 2 条）：识别 mod 工作台 + 扫描半径
            listing.CheckboxLabeled("DS_CraftAnyBenchToggle".Translate(), ref craftAnyBench,
                "DS_CraftAnyBenchToggleDesc".Translate());
            listing.Gap(6f);
            listing.Label("DS_CraftScanRadius".Translate(craftScanRadius, craftScanRadius * 2 + 1));
            float scan = craftScanRadius;
            scan = listing.Slider(scan, 2f, 20f);
            craftScanRadius = Mathf.Clamp(Mathf.RoundToInt(scan), 2, 20);
            listing.Gap(6f);

            // 配方展开（方案 A）：缺中间产物时摊成原料，一步成型
            listing.Label("DS_CraftExpandDepth".Translate(craftExpandDepth));
            float dep = craftExpandDepth;
            dep = listing.Slider(dep, 1f, 3f);
            craftExpandDepth = Mathf.Clamp(Mathf.RoundToInt(dep), 1, 3);
            listing.Label("DS_CraftExpandDepthDesc".Translate());
            listing.Gap(4f);
            listing.Label("DS_CraftExpandWork".Translate(Mathf.RoundToInt(craftExpandWorkFactor * 100f)));
            float fac = craftExpandWorkFactor;
            fac = listing.Slider(fac, 0f, 1f);
            craftExpandWorkFactor = Mathf.Clamp01(fac);
            listing.Label("DS_CraftExpandWorkDesc".Translate());
            listing.Gap(6f);
            listing.CheckboxLabeled("DS_PerfToggle".Translate(), ref perfOptimizationsEnabled,
                "DS_PerfToggleDesc".Translate());
            listing.Gap(6f);
            // 改的是那几条 stat 的因子（不是补丁），勾完立刻生效，不需要重启。
            // （Listing_Standard 的 CheckboxLabeled 不返回"变了吗"，自己比一次。）
            bool noFailBefore = noWorkFailure;
            listing.CheckboxLabeled("DS_NoWorkFailureToggle".Translate(), ref noWorkFailure,
                "DS_NoWorkFailureToggleDesc".Translate());
            if (noFailBefore != noWorkFailure) WorkFailureTuner.Apply();
            listing.Gap(6f);
            listing.Label("DS_WorkerBudget".Translate(workerCompletionsPerTick));
            float budget = workerCompletionsPerTick;
            budget = listing.Slider(budget, 0f, 120f);
            workerCompletionsPerTick = Mathf.RoundToInt(budget);
            listing.Label("DS_WorkerBudgetDesc".Translate());
            listing.Gap(6f);
            listing.Label("DS_FleckBudget".Translate(fleckBudgetPerFrame));
            float flecks = fleckBudgetPerFrame;
            flecks = listing.Slider(flecks, 0f, 5000f);
            fleckBudgetPerFrame = Mathf.RoundToInt(flecks);
            listing.Label("DS_FleckBudgetDesc".Translate());
            listing.Gap(24f);

            Text.Font = GameFont.Medium;
            listing.Label("DS_DebugSettings".Translate());
            Text.Font = GameFont.Small;
            listing.Gap(12f);

            listing.CheckboxLabeled("DS_EnableDebugLog".Translate(), ref enableDebugLog,
                "DS_EnableDebugLogDesc".Translate());

            listing.End();

            // 离散档位 ⇒ 一次拖动最多触发十几次，代价可以忽略（改完立刻生效：造价/研究改 def、
            // 电力把已建成建筑的 PowerOutput 重算一遍）。
            if (changed) DefMultipliers.ApplyAll();
        }

        /// <summary>
        /// 倍率滑条：滑的是**档位下标**，返回值是档位对应的倍率。
        /// 当前值不在档位上时（老存档里的 0.37 之类）取最近档显示，但**不动**玩家的值 ——
        /// 只有真的拖了才落到档位上。
        /// </summary>
        private static float MultiplierSlider(Listing_Standard listing, float current, ref bool changed)
        {
            int index = NearestLadderIndex(current);
            float value = Widgets.HorizontalSlider(listing.GetRect(22f), index, 0f,
                MultiplierLadder.Length - 1, true, null, null, null, 1f);
            int newIndex = Mathf.Clamp(Mathf.RoundToInt(value), 0, MultiplierLadder.Length - 1);

            float result = MultiplierLadder[newIndex];
            if (!Mathf.Approximately(result, current)) changed = true;
            return result;
        }

        private static int NearestLadderIndex(float value)
        {
            int best = 0;
            float bestDelta = float.MaxValue;
            for (int i = 0; i < MultiplierLadder.Length; i++)
            {
                float delta = Mathf.Abs(MultiplierLadder[i] - value);
                if (delta < bestDelta)
                {
                    bestDelta = delta;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// 把倍率自己拼成字符串再交给翻译串（<c>{0}</c>），**不要**在 keyed 里写
        /// <c>{0:0.##}×</c> 这类格式说明符 —— 实测线上出现过"造价倍率: X"（数字没渲染出来），
        /// 而且格式说明符还要看语言数据/语言包怎么处理。值自己格式化最稳。
        /// </summary>
        private static string FormatMultiplier(float mult)
        {
            return mult.ToString("0.###", CultureInfo.InvariantCulture) + "×";
        }
    }
}
