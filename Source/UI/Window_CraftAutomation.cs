using System;
using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// <b>制作自动化 · 三栏大窗口</b>（用户从界面稿里挑的 V4 结构）。
    ///
    /// <para>三个入口共用同一个窗口（单例）：<b>底部菜单栏按钮</b>（<c>MainButtonWorker_CraftAutomation</c>）、
    /// <b>制作代理的检查面板</b>（<c>ITab_BillAutomation</c> 上的按钮）、以及窗口内左上角的<b>建筑切换</b>。</para>
    ///
    /// <list type="bullet">
    /// <item><b>左栏</b>：状态筛选导航（全部 / 生产中 / 阻塞 / 待处理 / 已挂起 / 已完成，带计数）——
    ///   "一眼知道今天该修哪几条线"。</item>
    /// <item><b>中栏</b>：该代理的订单列表（状态点 + 产物图标 + 名称 + ∞/剩余）。</item>
    /// <item><b>右栏</b>：选中订单的详情 —— 模式 / 在产 x/y 台 / 状态、进度条与预计剩余、
    ///   所需材料（含核心现有库存，不足标红）、以及 +1 / +10 / 挂起 / 上移 / 下移 / 删除。</item>
    /// </list>
    ///
    /// <para>顶部常驻：建筑切换、总开关、超频（×1/×3/×6/×9）、添加配方…、耗电明细、统计。</para>
    /// </summary>
    public class Window_CraftAutomation : Window
    {
        // ===================================================================
        // 配色（取自界面稿 V4 的 CSS 变量）
        // ===================================================================
        private static readonly Color ColPanelDark = new Color(0.106f, 0.125f, 0.145f);
        private static readonly Color ColPanel = new Color(0.133f, 0.153f, 0.176f);
        private static readonly Color ColLite = new Color(0.173f, 0.200f, 0.227f);
        private static readonly Color ColLine = new Color(0.212f, 0.239f, 0.271f);
        private static readonly Color ColText = new Color(0.839f, 0.855f, 0.871f);
        private static readonly Color ColDim = new Color(0.529f, 0.561f, 0.596f);
        private static readonly Color ColAccent = new Color(0.788f, 0.663f, 0.380f);
        private static readonly Color ColOk = new Color(0.498f, 0.690f, 0.412f);
        private static readonly Color ColWarn = new Color(0.851f, 0.643f, 0.255f);
        private static readonly Color ColBad = new Color(0.769f, 0.333f, 0.247f);
        private static readonly Color ColInfo = new Color(0.435f, 0.620f, 0.769f);

        private const float NavWidth = 116f;
        private const float ListWidth = 236f;
        private const float TopHeight = 58f;

        private enum SKind
        {
            Ok,
            Warn,
            Bad,
            Idle,
            Done
        }

        // ===================================================================
        // 状态
        // ===================================================================

        private CompBillAutomation comp;
        private readonly List<CompBillAutomation> proxies = new List<CompBillAutomation>();
        private int nextProxyScanTick;

        private int navIndex;
        private CraftPlan sel;

        private Vector2 scrollList;
        private Vector2 scrollDetail;

        /// <summary>核心现有库存（材料行用）—— 每秒重算一次，别每帧遍历容器。</summary>
        private readonly Dictionary<ThingDef, int> stock = new Dictionary<ThingDef, int>();
        private int nextStockTick;

        private readonly List<CraftPlan> shown = new List<CraftPlan>();

        public Window_CraftAutomation(CompBillAutomation comp)
        {
            this.comp = comp;
            this.draggable = true;
            this.resizeable = true;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
            this.closeOnClickedOutside = false;
            this.onlyOneOfTypeAllowed = true;
            this.preventCameraMotion = true;   // 鼠标在窗口上时别让拖拽带着镜头跑
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(960f, 640f); }
        }

        /// <summary>三个入口共用这一个窗口（已经开着就只换目标建筑）。</summary>
        public static void OpenOrFocus(CompBillAutomation target)
        {
            Window_CraftAutomation existing = Find.WindowStack.WindowOfType<Window_CraftAutomation>();
            if (existing != null)
            {
                if (target != null) existing.SetComp(target);
                return;
            }
            // ★ AE2 风格（T1-④）：所有入口（制作自动化主按钮 / ITab / gizmo）统一打开新的 AE2 界面；
            //   旧面板没有退役 —— 可从新界面右下角的「打开制作面板」按钮进入（加配方/选材料/存储设置都还在那里）。
            Find.WindowStack.Add(new DigitalStorage.UI.Window_AE2CraftPanel(target));
        }

        private void SetComp(CompBillAutomation c)
        {
            if (c == null || c == comp) return;
            comp = c;
            sel = null;
            ClearCountBuffers();
            navIndex = 0;
            scrollList = Vector2.zero;
            scrollDetail = Vector2.zero;
        }

        // ===================================================================
        // 每帧刷新（列表 / 库存都按秒级节奏重算）
        // ===================================================================

        public override void WindowUpdate()
        {
            base.WindowUpdate();
            int now = Find.TickManager.TicksGame;

            if (now >= nextProxyScanTick)
            {
                nextProxyScanTick = now + 60;
                RescanProxies();
            }
            if (now >= nextStockTick)
            {
                nextStockTick = now + 60;
                RebuildStock();
            }
        }

        private void RescanProxies()
        {
            proxies.Clear();
            Map map = Find.CurrentMap;
            if (map == null) return;
            List<Building> all = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < all.Count; i++)
            {
                CompBillAutomation c = all[i].TryGetComp<CompBillAutomation>();
                if (c != null) proxies.Add(c);
            }
            if (comp == null || comp.parent == null || !comp.parent.Spawned)
            {
                comp = proxies.Count > 0 ? proxies[0] : null;
                sel = null;
            }
        }

        /// <summary>核心现有库存：所有可用核心的直接内容物，按 def 求和。</summary>
        private void RebuildStock()
        {
            stock.Clear();
            Map map = Find.CurrentMap;
            if (map == null) return;
            List<Building_StorageCore> cores = CoreFinder.AllUsableCores(map);
            for (int i = 0; i < cores.Count; i++)
            {
                ThingOwner held = cores[i].GetDirectlyHeldThings();
                if (held == null) continue;
                for (int k = 0; k < held.Count; k++)
                {
                    Thing t = held[k];
                    if (t == null || t.Destroyed || t.def == null) continue;
                    int cur;
                    stock.TryGetValue(t.def, out cur);
                    stock[t.def] = cur + Math.Max(0, t.stackCount);
                }
            }
        }

        private int StockOf(ThingDef def)
        {
            int n;
            return (def != null && stock.TryGetValue(def, out n)) ? n : 0;
        }

        // ===================================================================
        // 主绘制
        // ===================================================================

        public override void DoWindowContents(Rect inRect)
        {
            // 整窗底色 + 外框
            Widgets.DrawBoxSolid(inRect, ColPanelDark);
            Widgets.DrawBox(inRect, 1);

            Rect top = new Rect(inRect.x + 1f, inRect.y + 1f, inRect.width - 2f, TopHeight);
            DrawTop(top);

            Rect body = new Rect(inRect.x + 1f, top.yMax, inRect.width - 2f, inRect.yMax - top.yMax - 1f);
            if (comp == null)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(body, "DS_CA_NoProxy".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }

            Rect nav = new Rect(body.x, body.y, NavWidth, body.height);
            Rect list = new Rect(nav.xMax, body.y, ListWidth, body.height);
            Rect detail = new Rect(list.xMax, body.y, body.width - NavWidth - ListWidth, body.height);

            // 维持数量模式：让面板看到的计数与生产线调度一致（内部 30 tick 节流，不会每帧去数）
            Map map = Find.CurrentMap;
            if (map != null)
            {
                IList<CraftPlan> all = comp.PlansForReading;
                for (int i = 0; i < all.Count; i++)
                {
                    comp.RefreshTargetStateIfStale(all[i], map);
                }
            }

            DrawNav(nav);
            DrawList(list);
            DrawDetail(detail);
        }

        // ---------------------------------------------------------------- 顶部

        private void DrawTop(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, ColPanel);

            float y = rect.y + 4f;
            float x = rect.x + 6f;

            // 建筑切换
            string proxyLabel = (comp == null || comp.parent == null)
                ? "DS_CA_NoProxy".Translate().ToString()
                : comp.parent.LabelShort + "  " + comp.parent.Position.ToString();
            if (Widgets.ButtonText(new Rect(x, y, 250f, 24f), proxyLabel))
            {
                OpenProxyMenu();
            }
            x += 256f;

            if (comp != null)
            {
                // 总开关
                string state = comp.Enabled ? "DS_BA_On".Translate() : "DS_BA_Off".Translate();
                if (Widgets.ButtonText(new Rect(x, y, 132f, 24f), "DS_BA_ToggleState".Translate(state)))
                {
                    comp.Enabled = !comp.Enabled;
                }
                x += 138f;

                // 超频（点一次换一档）
                if (Widgets.ButtonText(new Rect(x, y, 168f, 24f),
                        "DS_BA_Overclock".Translate(comp.OverclockLabel())))
                {
                    comp.OverclockTier = (comp.OverclockTier + 1) % 4;
                }
                x += 174f;
            }

            float addW = Math.Min(150f, rect.xMax - x - 8f);
            if (addW > 40f && Widgets.ButtonText(new Rect(rect.xMax - addW - 6f, y, addW, 24f), "DS_CA_AddRecipe".Translate()))
            {
                if (comp != null) Find.WindowStack.Add(new Dialog_DS_AddCraft(comp));
            }

            y += 26f;

            // 耗电 / 统计
            Text.Font = GameFont.Tiny;
            if (comp != null)
            {
                GUI.color = ColDim;
                Widgets.Label(new Rect(rect.x + 6f, y, rect.width - 12f, 24f),
                    "DS_BA_Watts".Translate(
                        comp.CurrentWatts.ToString("#####0"),
                        comp.Props.basePowerWatts.ToString("#####0"),
                        comp.BenchWatts().ToString("#####0"),
                        comp.OverclockPowerMult.ToString("0"))
                    + "   ·   " + "DS_BA_Stats".Translate(comp.CompletedCount, comp.DroppedCount, comp.PlansForReading.Count));
                GUI.color = Color.white;
            }
            else
            {
                GUI.color = ColDim;
                Widgets.Label(new Rect(rect.x + 6f, y, rect.width - 12f, 24f), "DS_CA_NoProxyHint".Translate());
                GUI.color = Color.white;
            }
            Text.Font = GameFont.Small;
            GUI.color = Color.white;

            // 顶栏与主体之间的分隔线
            Widgets.DrawBoxSolid(new Rect(rect.x, rect.yMax - 1f, rect.width, 1f), ColLine);
        }

        private void OpenProxyMenu()
        {
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            for (int i = 0; i < proxies.Count; i++)
            {
                CompBillAutomation c = proxies[i];
                if (c == null || c.parent == null) continue;
                CompBillAutomation target = c;
                string label = target.parent.LabelShort + "  " + target.parent.Position.ToString()
                    + "（" + "DS_BA_Stats2".Translate(target.CompletedCount, target.PlansForReading.Count) + "）";
                opts.Add(new FloatMenuOption(label, delegate { SetComp(target); }));
            }
            if (opts.Count == 0) opts.Add(new FloatMenuOption("DS_CA_NoProxy".Translate(), null));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        // ---------------------------------------------------------------- 左：状态导航

        private void DrawNav(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, ColPanelDark);
            Widgets.DrawBoxSolid(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), ColLine);

            float y = rect.y + 4f;
            Widgets.Label(new Rect(rect.x + 8f, y, rect.width - 12f, 20f), "DS_CA_Orders".Translate());
            y += 22f;

            for (int i = 0; i < NavCount; i++)
            {
                SKind kind = (SKind)i;
                int count = CountOf(kind);
                Rect row = new Rect(rect.x + 3f, y, rect.width - 6f, 24f);

                if (navIndex == i) Widgets.DrawBoxSolid(row, ColLite);
                else if (Mouse.IsOver(row)) Widgets.DrawBoxSolid(row, ColPanel);

                GUI.color = (navIndex == i) ? Color.white : ColText;
                Widgets.Label(new Rect(row.x + 6f, row.y + 2f, row.width - 40f, 20f), NavLabel(kind).Translate());
                Text.Anchor = TextAnchor.MiddleRight;
                GUI.color = CountColor(kind);
                Widgets.Label(new Rect(row.xMax - 34f, row.y + 2f, 30f, 20f), count.ToString());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;

                if (Widgets.ButtonInvisible(row))
                {
                    navIndex = i;
                    sel = null;
                }
                y += 25f;
            }
        }

        private const int NavCount = 6;

        private static string NavLabel(SKind k)
        {
            switch (k)
            {
                case SKind.Ok: return "DS_CA_NavOk";
                case SKind.Bad: return "DS_CA_NavBad";
                case SKind.Warn: return "DS_CA_NavWarn";
                case SKind.Idle: return "DS_CA_NavIdle";
                case SKind.Done: return "DS_CA_NavDone";
                default: return "DS_CA_NavAll";
            }
        }

        private int CountOf(SKind kind)
        {
            if (comp == null) return 0;
            IList<CraftPlan> plans = comp.PlansForReading;
            int n = 0;
            for (int i = 0; i < plans.Count; i++)
            {
                if (KindOf(plans[i]) == kind) n++;
            }
            return n;
        }

        private static Color ColorOf(SKind k)
        {
            switch (k)
            {
                case SKind.Ok: return ColOk;
                case SKind.Bad: return ColBad;
                case SKind.Warn: return ColWarn;
                case SKind.Done: return ColInfo;
                default: return ColDim;
            }
        }

        private static Color CountColor(SKind k)
        {
            return (k == SKind.Idle) ? ColDim : ColorOf(k);
        }

        // ---------------------------------------------------------------- 中：订单列表

        private void DrawList(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, ColPanelDark);
            Widgets.DrawBoxSolid(new Rect(rect.xMax - 1f, rect.y, 1f, rect.height), ColLine);

            BuildShown();

            Rect inner = new Rect(0f, 0f, rect.width - 18f, Math.Max(rect.height, shown.Count * 28f + 6f));
            Rect view = new Rect(rect.x + 2f, rect.y + 3f, rect.width - 4f, rect.height - 6f);
            Widgets.BeginScrollView(view, ref scrollList, inner);

            float y = 0f;
            if (shown.Count == 0)
            {
                GUI.color = ColDim;
                Widgets.Label(new Rect(4f, y, inner.width - 8f, 40f), "DS_CA_NoOrder".Translate());
                GUI.color = Color.white;
            }
            for (int i = 0; i < shown.Count; i++)
            {
                CraftPlan plan = shown[i];
                Rect row = new Rect(2f, y, inner.width - 4f, 26f);
                bool selected = (plan == sel);

                if (selected) Widgets.DrawBoxSolid(row, ColLite);
                else if (Mouse.IsOver(row)) Widgets.DrawBoxSolid(row, ColPanel);

                SKind kind = KindOf(plan);
                Widgets.DrawBoxSolid(new Rect(row.x + 5f, row.y + 10f, 6f, 6f), ColorOf(kind));

                ThingDef prod = CraftCategories.MainProduct(plan.recipe);
                if (prod != null)
                {
                    Widgets.DefIcon(new Rect(row.x + 16f, row.y + 3f, 20f, 20f), prod);
                }

                string name = (plan.recipe == null) ? "?" : plan.recipe.LabelCap.ToString();
                GUI.color = (plan.suspended || plan.Done) ? ColDim : ColText;
                Widgets.Label(new Rect(row.x + 40f, row.y + 4f, row.width - 96f, 20f), name);
                GUI.color = Color.white;

                Text.Anchor = TextAnchor.MiddleRight;
                GUI.color = ColDim;
                string badge;
                float badgeW = 30f;
                if (plan.mode == CraftPlan.ModeCount) badge = plan.remaining.ToString();
                else if (plan.mode == CraftPlan.ModeTarget)
                {
                    badge = plan.countedCount + "/" + plan.targetCount;   // 当前 / 目标
                    badgeW = 52f;
                }
                else badge = "∞";
                Widgets.Label(new Rect(row.xMax - badgeW - 22f, row.y + 4f, badgeW, 20f), badge);
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;

                // 细进度条压在行底
                CraftLine first = (plan.lines.Count > 0) ? plan.lines[0] : null;
                if (first != null && first.HasWork)
                {
                    Widgets.DrawBoxSolid(new Rect(row.x + 40f, row.yMax - 5f, row.width - 90f, 2f), ColLine);
                    Widgets.DrawBoxSolid(new Rect(row.x + 40f, row.yMax - 5f, (row.width - 90f) * Mathf.Clamp01(first.Progress01), 2f), ColAccent);
                }

                if (Widgets.ButtonInvisible(row)) SelectPlan(plan);
                y += 28f;
            }

            Widgets.EndScrollView();
        }

        private void BuildShown()
        {
            shown.Clear();
            if (comp == null) return;
            IList<CraftPlan> plans = comp.PlansForReading;
            SKind want = (SKind)navIndex;
            for (int i = 0; i < plans.Count; i++)
            {
                if (KindOf(plans[i]) == want) shown.Add(plans[i]);
            }
        }

        // ---------------------------------------------------------------- 右：详情

        private void DrawDetail(Rect rect)
        {
            Widgets.DrawBoxSolid(rect, ColPanel);

            if (sel == null || !comp.PlansForReading.Contains(sel))
            {
                sel = null;
                GUI.color = ColDim;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(rect, "DS_CA_PickOrder".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                return;
            }

            CraftPlan plan = sel;
            ThingDef prod = CraftCategories.MainProduct(plan.recipe);

            Rect inner = new Rect(0f, 0f, rect.width - 20f, Math.Max(rect.height - 12f, 580f));
            Widgets.BeginScrollView(new Rect(rect.x + 6f, rect.y + 6f, rect.width - 10f, rect.height - 12f),
                ref scrollDetail, inner);

            float x = 4f;
            float y = 0f;

            // 标题行
            if (prod != null) Widgets.DefIcon(new Rect(x, y, 42f, 42f), prod);
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(x + 50f, y, inner.width - 60f, 26f),
                (plan.recipe == null) ? "?" : plan.recipe.LabelCap.ToString());
            Text.Font = GameFont.Small;
            GUI.color = ColDim;
            Widgets.Label(new Rect(x + 50f, y + 24f, inner.width - 60f, 20f),
                "DS_CA_Sub".Translate(
                    CraftCategories.LabelKey(CraftCategories.Of(plan.recipe)).Translate(),
                    SkillText(plan.recipe)));
            GUI.color = Color.white;
            y += 50f;

            // 三行信息
            SKind kind = KindOf(plan);
            y = InfoRow(inner, x, y, "DS_CA_Mode".Translate(), ModeText(plan), ColText);
            y = InfoRow(inner, x, y, "DS_CA_Running".Translate(),
                "DS_BA_Lines".Translate(plan.lines.Count, comp.UsableBenchCountFor(plan.recipe)).ToString(), ColText);
            y = InfoRow(inner, x, y, "DS_CA_Status".Translate(), StatusText(plan), ColorOf(kind));
            y += 6f;

            // 进度
            CraftLine first = (plan.lines.Count > 0) ? plan.lines[0] : null;
            float pct = (first != null && first.HasWork) ? Mathf.Clamp01(first.Progress01) : 0f;
            Widgets.Label(new Rect(x, y, inner.width - 8f, 20f), "DS_CA_Progress".Translate());
            y += 20f;
            Rect bar = new Rect(x, y, inner.width - 10f, 18f);
            Widgets.DrawBoxSolid(bar, ColLite);
            Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width * pct, bar.height), ColAccent);
            Widgets.DrawBox(bar, 1);
            y += 22f;
            GUI.color = ColDim;
            string progLine = "DS_CA_Percent".Translate((pct * 100f).ToString("0")).ToString();
            if (first != null && first.HasWork)
            {
                progLine += "   ·   " + "DS_CA_RemainTime".Translate(RemainSeconds(first).ToString("0")).ToString();
            }
            Widgets.Label(new Rect(x, y, inner.width - 8f, 20f), progLine);
            GUI.color = Color.white;
            y += 26f;

            // 材料
            Widgets.Label(new Rect(x, y, inner.width - 8f, 20f), "DS_CA_Needs".Translate());
            y += 20f;
            y = DrawMaterials(plan, first, inner, x, y);
            y += 8f;

            // ---- 操作（自动换行：面板可缩放，硬排一行一定会被切掉）----
            FlowBegin(x, y, inner.width - 8f);

            FlowButton("DS_CA_ModeBtn".Translate(ModeText(plan)).ToString(), 132f, delegate
            {
                comp.CycleMode(plan);
                ClearCountBuffers();
            });

            bool countMode = plan.mode == CraftPlan.ModeCount;
            bool targetMode = plan.mode == CraftPlan.ModeTarget;
            if (countMode || targetMode)
            {
                // 数量可以直接改：−10 / −1 / [输入框] / +1 / +10（用户要求：能输入具体数量，而且能减）
                FlowButton("−10", 42f, delegate { plan.AddCount(-10); ClearCountBuffers(); });
                FlowButton("−1", 36f, delegate { plan.AddCount(-1); ClearCountBuffers(); });

                Rect numRect = FlowRect(78f);
                if (countMode)
                {
                    Widgets.TextFieldNumeric(numRect, ref plan.remaining, ref remainingBuf, 0f, 1E+09f);
                }
                else
                {
                    Widgets.TextFieldNumeric(numRect, ref plan.targetCount, ref targetBuf, 0f, 1E+09f);
                }

                FlowButton("+1", 36f, delegate { plan.AddCount(1); ClearCountBuffers(); });
                FlowButton("+10", 42f, delegate { plan.AddCount(10); ClearCountBuffers(); });
            }

            FlowButton(plan.suspended ? "DS_BA_Resume".Translate().ToString() : "DS_BA_Suspend".Translate().ToString(),
                60f, delegate { plan.suspended = !plan.suspended; });
            FlowButton("DS_CA_Up".Translate().ToString(), 56f, delegate { comp.MovePlan(plan, -1); });
            FlowButton("DS_CA_Down".Translate().ToString(), 56f, delegate { comp.MovePlan(plan, 1); });

            if (targetMode)
            {
                FlowButton("DS_CA_PauseWhenSatisfied".Translate().ToString() + "：" +
                           (plan.pauseWhenSatisfied ? "DS_BA_On".Translate().ToString() : "DS_BA_Off".Translate().ToString()),
                    146f, delegate
                    {
                        plan.pauseWhenSatisfied = !plan.pauseWhenSatisfied;
                        plan.countedTick = 0;
                    });
            }

            if (countMode || targetMode)
            {
                FlowButton("DS_CA_SetForever".Translate().ToString(), 96f, delegate { plan.SetForever(); });
            }

            FlowButton("DS_CA_Delete".Translate().ToString(), 70f, delegate
            {
                comp.RemovePlan(plan);
                SelectPlan(null);
            });
            if (sel == null)
            {
                Widgets.EndScrollView();
                return;
            }

            Widgets.EndScrollView();
        }

        // ===================================================================
        // 按钮流式布局（自动换行）
        // ===================================================================

        private float flowX, flowY, flowStartX, flowMaxX;
        private const float FlowRowH = 26f;
        private const float FlowGap = 4f;

        /// <summary>输入框缓冲（次数 / 维持数量各一份）。切换选中项时置空，交给 TextFieldNumeric 重新初始化。</summary>
        private string remainingBuf;
        private string targetBuf;

        private void FlowBegin(float x, float y, float maxX)
        {
            flowStartX = x;
            flowX = x;
            flowY = y;
            flowMaxX = maxX;
        }

        private Rect FlowRect(float w)
        {
            if (flowX + w > flowMaxX && flowX > flowStartX)
            {
                flowX = flowStartX;
                flowY += FlowRowH + FlowGap;
            }
            Rect r = new Rect(flowX, flowY, w, FlowRowH);
            flowX += w + FlowGap;
            return r;
        }

        private void FlowButton(string label, float w, Action onClick)
        {
            if (Widgets.ButtonText(FlowRect(w), label) && onClick != null) onClick();
        }

        private float FlowEnd()
        {
            return flowY + FlowRowH;
        }

        /// <summary>数值在别处被改过（±按钮/换选中项）⇒ 丢掉输入框缓冲，让它从新值重新初始化。</summary>
        private void ClearCountBuffers()
        {
            remainingBuf = null;
            targetBuf = null;
        }

        private void SelectPlan(CraftPlan plan)
        {
            sel = plan;
            ClearCountBuffers();
        }

        /// <summary>
        /// 本件预计剩余秒数。速率要把**等级倍率 × 超频倍率**算进去 —— 否则 9GHz 下估出来的时间会大 9 倍，
        /// 玩家一眼就看得出不对（这里只做量级估计，不追帧）。
        /// </summary>
        private float RemainSeconds(CraftLine line)
        {
            float rate = Math.Max(0.0001f, line.BaseRate);
            if (comp != null) rate *= Math.Max(0.01f, comp.Props.workSpeedMult) * comp.OverclockSpeedMult;
            return Math.Max(0f, line.WorkLeft) / rate / 60f;
        }

        /// <summary>模式的一行文字（三处共用）：无限 / 剩余 N / 维持 当前/目标。</summary>
        private static string ModeText(CraftPlan plan)
        {
            if (plan == null) return "";
            if (plan.mode == CraftPlan.ModeCount) return "DS_BA_Remain".Translate(plan.remaining).ToString();
            if (plan.mode == CraftPlan.ModeTarget)
            {
                return "DS_CA_Target".Translate(plan.countedCount, plan.targetCount).ToString();
            }
            return "DS_BA_Forever".Translate().ToString();
        }

        private static string SkillText(RecipeDef r)
        {
            if (r == null || r.skillRequirements == null || r.skillRequirements.Count == 0) return "";
            for (int i = 0; i < r.skillRequirements.Count; i++)
            {
                SkillRequirement req = r.skillRequirements[i];
                if (req != null) return req.skill.LabelCap + " " + req.minLevel;
            }
            return "";
        }

        private float InfoRow(Rect inner, float x, float y, string label, string value, Color valueColor)
        {
            GUI.color = ColDim;
            Widgets.Label(new Rect(x, y, 70f, 20f), label);
            GUI.color = valueColor;
            Widgets.Label(new Rect(x + 74f, y, inner.width - 84f, 20f), value);
            GUI.color = Color.white;
            return y + 21f;
        }

        /// <summary>
        /// 材料列表：优先显示**这一轮原版实际选中的料**（def + 数量 + 核心库存），
        /// 还没取到活时退回显示配方槽位摘要（例："20x 钢铁"）。
        /// </summary>
        private float DrawMaterials(CraftPlan plan, CraftLine line, Rect inner, float x, float y)
        {
            bool any = false;

            if (line != null && line.HasWork && line.Ingredients != null)
            {
                for (int i = 0; i < line.Ingredients.Length && i < line.Counts.Length; i++)
                {
                    Thing t = line.Ingredients[i];
                    if (t == null || t.def == null) continue;
                    any = true;
                    y = MatRow(inner, x, y, t.def, line.Counts[i]);
                }
            }

            if (!any && plan.recipe != null && plan.recipe.ingredients != null)
            {
                for (int i = 0; i < plan.recipe.ingredients.Count; i++)
                {
                    IngredientCount ic = plan.recipe.ingredients[i];
                    if (ic == null) continue;
                    any = true;
                    ThingDef only = ic.IsFixedIngredient ? ic.FixedIngredient : null;
                    y = MatRow(inner, x, y, only, -1, ic.Summary);
                }
            }

            if (!any)
            {
                GUI.color = ColDim;
                Widgets.Label(new Rect(x, y, inner.width - 8f, 20f), "DS_CA_NoMaterials".Translate());
                GUI.color = Color.white;
                y += 20f;
            }
            return y;
        }

        private float MatRow(Rect inner, float x, float y, ThingDef def, int need, string textOverride = null)
        {
            Rect row = new Rect(x, y, inner.width - 10f, 22f);
            if (def != null) Widgets.DefIcon(new Rect(row.x, row.y + 2f, 18f, 18f), def);

            string name = (textOverride != null) ? textOverride : ((def == null) ? "?" : def.LabelCap.ToString());
            Widgets.Label(new Rect(row.x + 22f, row.y + 2f, row.width - 130f, 20f), name);

            Text.Anchor = TextAnchor.MiddleRight;
            if (need >= 0)
            {
                int have = StockOf(def);
                bool lack = have < need;
                GUI.color = lack ? ColBad : ColDim;
                Widgets.Label(new Rect(row.xMax - 120f, row.y + 2f, 116f, 20f),
                    "DS_CA_Have".Translate(need, have));
                GUI.color = Color.white;
            }
            Text.Anchor = TextAnchor.UpperLeft;
            return y + 23f;
        }

        // ===================================================================
        // 状态判定（导航 / 列表 / 详情共用，避免三处漂移）
        // ===================================================================

        private static SKind KindOf(CraftPlan plan)
        {
            if (plan == null) return SKind.Idle;
            if (plan.Done) return SKind.Done;
            if (plan.suspended) return SKind.Idle;
            // 维持数量"达标暂停"= 这一单不用管了 ⇒ 归到"已完成"那一档（它会在产物被消耗后自动恢复）
            if (plan.mode == CraftPlan.ModeTarget && plan.paused) return SKind.Done;

            CraftLine line = (plan.lines.Count > 0) ? plan.lines[0] : null;
            if (line != null && line.HasWork) return SKind.Ok;
            if (plan.lines.Count == 0 && plan.mode != CraftPlan.ModeTarget) return SKind.Bad;   // 范围内没有可用工作台
            if (plan.lines.Count == 0 && plan.mode == CraftPlan.ModeTarget && !plan.paused) return SKind.Warn;

            string k = (line == null) ? null : line.BlockKey;
            if (k == null) return SKind.Ok;
            switch (k)
            {
                case "DS_BA_Block_Material":
                case "DS_BA_NoCoreMaterial":
                case "DS_BA_Block_Skill":
                case "DS_BA_Block_Research":
                case "DS_BA_Block_Unsupported":
                case "DS_BA_Block_Restricted":
                case "DS_BA_NoBill":
                    return SKind.Bad;                          // 要玩家动手
                default:
                    return SKind.Warn;                         // 等条件（断电 / 被占用 / 站位格 / 缺燃料）
            }
        }

        private static string StatusText(CraftPlan plan)
        {
            if (plan.Done) return "DS_BA_PlanFinished".Translate().ToString();
            if (plan.suspended) return "DS_BA_Suspended".Translate().ToString();
            if (plan.mode == CraftPlan.ModeTarget && plan.paused)
            {
                return "DS_CA_TargetPaused".Translate(plan.countedCount, plan.targetCount).ToString();
            }
            CraftLine line = (plan.lines.Count > 0) ? plan.lines[0] : null;
            if (line == null)
            {
                return (plan.mode == CraftPlan.ModeTarget && plan.countedCount < plan.targetCount)
                    ? "DS_BA_NoBench".Translate().ToString()
                    : "DS_CA_TargetPaused".Translate(plan.countedCount, plan.targetCount).ToString();
            }
            if (line.HasWork) return "DS_CA_Working".Translate().ToString();
            if (line.BlockKey.NullOrEmpty()) return "DS_CA_Waiting".Translate().ToString();
            return line.BlockKey.Translate().ToString();
        }
    }
}
