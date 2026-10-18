// =====================================================================================
//  【本地新增文件／整块替换】Window_AE2CraftPanel —— AE2 材质 + **原版 DS 全部功能**
// -------------------------------------------------------------------------------------
//  用户要求（2026-10-05）：
//    · 材质用 AE2 的（AE2Draw：面板/凹槽/凸起按钮/行/进度条/侧面可拖动条）
//    · **原版 DS 面板的功能按钮一个都不能少**：添加配方、模式、−10/−1/+1/+10、挂起/恢复、
//      上移/下移、改为无限、删除、达标即暂停、订单统计（可点筛选）、所需材料（含核心库存、
//      不足标红）、当前进度、在产台数
//    · 再加一个**改制作材料**按钮（落到 CraftPlan.allowedStuff —— 未完成品兼容那条链认它）
//
//  布局（AE2 材质）：
//    ┌ 标题带（代理名 + 坐标 + ×）─────────────────────────────────────────────┐
//    │ [制作自动化：开] [超频：3 GHz]                            [添加配方…]    │
//    │ 耗电… · 已产出… 落地… 配方…                                            │
//    │ 订单 N│正在生产 N│等待中 N│阻塞 N│已挂起 N│已完成 N   ← 点一下筛选        │
//    ├ 左：核心实物（点取物）┬ 中：订单列表 ┬ 右：详情 + 全部按钮 ──────────────┤
//    └──────────────────────┴─────────────┴──────────────────────────────────┘
//
//  ⚠️ 约束：不用 BeginScrollView（用 AE2Draw.DragBar 自绘滚动）；Translate 结果一律 .ToString()
//     （C# 7.3 里 string 与 TaggedString 混用的三元会报 CS8957）。
// =====================================================================================
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using DigitalStorage.AI;
using DigitalStorage.Components;
using DigitalStorage.Core;

namespace DigitalStorage.UI
{
    public class Window_AE2CraftPanel : Window
    {
        private const int F_All = 0, F_Running = 1, F_Waiting = 2, F_Blocked = 3, F_Suspended = 4, F_Done = 5;

        private CompBillAutomation comp;   // ★ 目标④：可为 null ⇒ 打开时自动挑一台代理（主按钮传 null）
        private int filter = F_All;
        private CraftPlan sel;
        private float scrollOrders, scrollCore;
        private string searchCore = "";   // ★ 用户要求：核心列物顶部搜索栏
        private string countBuf;
        private CraftPlan countBufPlan;
        private int countBufShown = -1;   // ★ 槽里当前显示的数字（用来发现"外部改了数量"）

        private const float HeaderH = 24f;
        private const float InfoH = 18f;
        private const float FilterH = 22f;
        private const float RowH = 40f;   // ★ 订单行（预览图：大而行间宽松）
        private const float CoreRowH = 30f;   // ★ 存储核心行（预览图：大按钮）

        public Window_AE2CraftPanel(CompBillAutomation comp)
        {
            this.comp = comp;
            this.doCloseX = false;
            this.closeOnClickedOutside = false;
            this.absorbInputAroundWindow = false;   // ★ 让环世界继续处理全局按键（空格/加速/摄像机）
            this.onlyOneOfTypeAllowed = true;
            this.draggable = true;
            // ★★ 缩放手柄必须关掉（实测 bug 的真凶）：
            //   RimWorld 的 WindowResizer.DoResizeControl 会在窗口**右下角放一个 24×24 的
            //   隐形 Widgets.ButtonImage 手柄**。详情栏最后几行按钮正好压在那个角上 ⇒
            //   点击被缩放手柄先吃掉 ⇒ 实机表现"右下角这一片按钮点不动、
            //   而且越靠右下角越点不动"（用户反馈：风格按钮全无反应、材料按钮最右一小段无反应）。
            //   面板布局本来就全按 rect 自适应，默认 960×540 够用，不需要拖拽缩放。
            this.resizeable = false;
            this.doWindowBackground = false;

            // ★ 打开面板时**扫一次**维持数量，保证一打开看到的就是准的。
            //   之后 UI **不再触发扫描**（Draw 里只读缓存），刷新只发生在：
            //   产出时（BillCraftFunnel ⇒ comp.RecountNow）与调度侧 30 tick 兜底。
            RefreshTargetCountsOnce(comp);
        }

        /// <summary>把所有"维持数量"订单的产物数扫一遍（只在面板打开时调一次）。</summary>
        private static void RefreshTargetCountsOnce(CompBillAutomation comp)
        {
            if (comp == null) return;
            Map map = Find.CurrentMap;
            if (map == null) return;
            IList<CraftPlan> all = comp.PlansForReading;
            if (all == null) return;
            for (int i = 0; i < all.Count; i++)
            {
                CraftPlan p = all[i];
                if (p == null) continue;
                try { comp.RecountNow(p); }
                catch (Exception e) { Log.ErrorOnce("[DigitalStorage] 面板打开时重数失败：" + e, 771234); }
            }
        }

        public override Vector2 InitialSize { get { return new Vector2(960f, 540f); } }

        // =================================================================================
        public override void DoWindowContents(Rect inRect)
        {
            try
            {
            AE2Draw.HandlePauseHotkey();   // ★ 必须在最开头：空格暂停/继续（放在任何 return 之前）
            if (comp == null || comp.parent == null)
            {
                comp = FindAnyProxy();
                if (comp == null)
                {
                    // ★ 目标④：底栏主按钮会传 null 进来 —— 自动挑一台；一台都没有就明确提示
                    //   （原版没有这句就会"点了没反应"，这是真 bug）
                    bool cc0;
                    Rect r0 = AE2Draw.WindowFrame(inRect.ContractedBy(2f), "DS_AE2_Title".Translate().ToString(), out cc0);
                    if (cc0) { Close(); return; }
                    AE2Draw.TextIn(new Rect(r0.x + 6f, r0.y + 6f, r0.width - 12f, 20f), "DS_CA_NoProxyHint".Translate().ToString(), 1f, AE2Draw.TextCol, TextAnchor.MiddleLeft);
                    return;
                }
            }
            Map map = comp.parent.Map;

            // ★ 第 11 轮：窗口可缩放 ⇒ 太小时必须先挡住（否则右栏宽度会变成负数 ⇒ GUI 报错、界面点不动）
            if (inRect.width < 620f || inRect.height < 360f)
            {
                bool ccSmall;
                Rect rs = AE2Draw.WindowFrame(inRect.ContractedBy(2f), "DS_AE2_Title".Translate().ToString(), out ccSmall);
                if (ccSmall) { Close(); return; }
                AE2Draw.TextIn(new Rect(rs.x + 6f, rs.y + 6f, rs.width - 12f, 20f), "DS_AE2_TooSmall".Translate().ToString(), 1f, AE2Draw.Bad, TextAnchor.MiddleLeft);
                return;
            }
            if (map == null) { Close(); return; }

            bool closeClicked;
            string title = comp.parent.LabelShort.ToString() + "　" + comp.parent.Position.ToString();

            // ============================================================================
            //  ★ 按模板重做窗口骨架：左侧竖排图标栏 + 标题栏（内嵌搜索/关闭）+ 工具行 + 状态带 + 三栏
            //     （不再用 AE2Draw.WindowFrame —— 那是"标题带+关闭钮"的旧骨架）
            // ============================================================================
            Rect full = inRect.ContractedBy(2f);
            AE2Draw.WindowBg(full);

            // ── 左侧竖排方形图标按钮栏（AE2 标志性）──
            float railW = 28f;
            Rect rail = new Rect(full.x + 4f, full.y + 4f, railW, full.height - 8f);
            DrawToolRail(rail);

            // ── 内容区起点（让开左栏）──
            float cx0 = rail.xMax + 6f;
            float cw0 = full.xMax - cx0 - 4f;
            float yy = full.y + 4f;

            // ── 标题栏：标题 + 搜索框 + 关闭 ──
            Rect head = new Rect(cx0, yy, cw0, HeaderH + 4f);
            AE2Draw.ColumnHead(head);
            // ★ 切代理：点击区域**只覆盖标题文字本身**（原来 head.width-200 几乎整条标题栏 ⇒
            //   点一下就在鼠标处弹出 FloatMenu，正好盖住下面的过滤按钮。实测踩过）
            float titleW = Mathf.Ceil(Verse.Text.CalcSize(title).x) + 4f;
            AE2Draw.TextIn(new Rect(head.x + 6f, head.y, titleW + 14f, head.height), title, 1f, AE2Draw.TextHi, TextAnchor.MiddleLeft);
            Rect proxyR = new Rect(head.x + 4f, head.y + 2f, titleW + 12f, head.height - 4f);
            if (Mouse.IsOver(proxyR))
            {
                Widgets.DrawBoxSolid(proxyR, AE2Draw.Button);
                if (Event.current != null && Event.current.type == EventType.MouseDown && Event.current.button == 0)
                {
                    Event.current.Use();
                    OpenProxyMenu();
                }
            }
            // ★ 标题旁的 ▼ 箭头已按用户要求去掉（悬停变按钮底已经足够提示可点）

            Rect closeR = new Rect(head.xMax - 20f, head.y + 3f, 18f, 18f);
            if (AE2Draw.AE2TextButton(closeR, "×")) { Close(); return; }

            Rect searchR = new Rect(head.xMax - 20f - 6f - 260f, head.y + 5f, 260f, 16f);   // ★ 156→260：搜索栏加宽
            searchCore = AE2Draw.TextField(searchR, searchCore);

            yy = head.yMax + 2f;

            // ── 工具行：过滤方按钮 + 动作方按钮 ──
            IList<CraftPlan> plans0 = comp.PlansForReading;
            if (plans0 == null) plans0 = new List<CraftPlan>();
            yy = DrawToolRow(cx0, yy, cw0, plans0);

            // ── 状态带 ──
            Rect info = new Rect(cx0, yy, cw0, InfoH);
            AE2Draw.Sunken(info, AE2Draw.BarBg);
            string watts = "DS_BA_Watts".Translate(
                comp.CurrentWatts.ToString("#####0"),
                comp.Props.basePowerWatts.ToString("#####0"),
                comp.BenchWatts().ToString("#####0"),
                comp.OverclockPowerMult.ToString("0")).ToString();
            string stats = "DS_BA_Stats".Translate(comp.CompletedCount, comp.DroppedCount, comp.PlansForReading.Count).ToString();
            AE2Draw.TextIn(new Rect(info.x + 5f, info.y, info.width - 10f, info.height), watts + "　·　" + stats, 1f, AE2Draw.Hi, TextAnchor.MiddleLeft);
            yy = info.yMax + 3f;

            IList<CraftPlan> plans = comp.PlansForReading;
            if (plans == null) plans = new List<CraftPlan>();
            if (sel != null && !plans.Contains(sel)) sel = null;
            if (sel == null && plans.Count > 0) sel = plans[0];

            float listH = full.yMax - yy - 4f;
            if (listH < 60f) listH = 60f;
            // ★ 三栏宽度按模板实测比例（模板 233/290/277 ⇒ 0.28 / 0.35 / 剩余）
            float colAvail = full.xMax - 4f - cx0;
            float coreW = Mathf.Clamp(colAvail * 0.28f, 180f, 300f);
            float orderW = Mathf.Clamp(colAvail * 0.35f, 220f, 380f);
            Rect coreCol = new Rect(cx0, yy, coreW, listH);
            Rect orderCol = new Rect(coreCol.xMax + 5f, yy, orderW, listH);
            Rect detailCol = new Rect(orderCol.xMax + 5f, yy, full.xMax - 4f - orderCol.xMax - 5f, listH);

            DrawCore(coreCol, map);
            DrawOrders(orderCol, plans);
            DrawDetail(detailCol, map);
            }
            catch (Exception __uiEx) { Log.ErrorOnce("[DigitalStorage] AE2 界面绘制异常（只记一次，界面不会卡死）：" + __uiEx, 771001); }
        }

        // =================================================================================
        //  ★ 新骨架部件：左竖排栏 / 工具行（方按钮）
        // =================================================================================

        /// <summary>左侧竖排方形图标按钮栏（AE2 标志性）。图标用 ASCII，避免字体缺字变方框。</summary>
        private void DrawToolRail(Rect rail)
        {
            float s = Mathf.Min(24f, rail.width - 4f);
            float x = rail.x + (rail.width - s) / 2f;
            // 图标语义：^核心 / #列表 / *风格 / II挂起 / <>代理 / =加配方 / []范围
            // ★ 纯 ASCII 图标（SimSun 不含 ⚙/⏸ 等符号 ⇒ 会渲染成方块。实测踩过）
            // ★ 布局（7 个）：^ 核心 / II 全部暂停 / > 全部开始 / II 单个挂起 / <> 切代理 / = 加配方 / [] 范围
            string[] icons = new string[] { "^", "II", ">", "II", "<>", "=", "[]" };
            string[] tips = new string[]
            {
                "DS_AE2_CoreItems".Translate().ToString(),
                "DS_CA_SuspendAll".Translate().ToString(),
                "DS_CA_ResumeAll".Translate().ToString(),
                "DS_BA_Suspend".Translate().ToString(),
                "DS_CA_SwitchProxy".Translate().ToString(),
                "DS_CA_AddRecipe".Translate().ToString(),
                "DS_BA_ShowRange".Translate().ToString(),
            };
            for (int i = 0; i < icons.Length; i++)
            {
                Rect b = new Rect(x, rail.y + i * (s + 3f), s, s);
                if (b.yMax > rail.yMax) break;
                bool active = (i == 3 && sel != null && sel.suspended)   // 只有"单个挂起"是切换态；1/2 是动作
                           || (i == 6 && comp != null && comp.ShowRange);   // ★ 范围开关的选中态
                if (AE2Draw.AE2TextButton(b, icons[i], active))
                {
                    switch (i)
                    {
                        case 1:   // ★ 全部暂停
                            SetAllSuspended(true);
                            break;
                        case 2:   // ★ 全部开始
                            SetAllSuspended(false);
                            break;
                        case 3:   // 挂起/继续（单个）
                            if (sel != null) sel.suspended = !sel.suspended;
                            break;
                        case 4:   // 切换代理
                            OpenProxyMenu();
                            break;
                        case 5:   // 打开加配方
                            Find.WindowStack.Add(new Dialog_AE2AddRecipe(comp));
                            break;
                        case 6:   // ★ 范围显示开关（原 gizmo 的功能，挪进面板）
                            if (comp != null) comp.ToggleShowRange();
                            break;
                        case 0:   // 选中核心建筑
                        {
                            Building_StorageCore core0 = FirstCore(comp.parent.Map);
                            if (core0 != null) { Find.Selector.ClearSelection(); Find.Selector.Select(core0, false, false); Close(); }
                            break;
                        }
                    }
                }
                string tip = tips[i];
                if (!string.IsNullOrEmpty(tip) && Mouse.IsOver(b)) TooltipHandler.TipRegion(b, tip);
            }
        }

        /// <summary>
        /// ★ 手绘"合成树"图标：上 1 点 → 两条斜线分叉 → 下 2 点。
        /// <para>为什么不用字符：SimSun 7pt 下 ∧ / ├ / ⑂ 都渲染不清（而且可能缺字变方块）；
        /// 目标图的图标本来就是**几何形状**，所以直接按像素画 —— 任意缩放都锐利。</para>
        /// </summary>
        private static void TreeGlyph(Rect r)
        {
            // 网格 10 列：上节点 4..6；下节点 0..2 与 7..9（中间留 3 列空隙）
            float u = r.width / 10f;
            Color c = AE2Draw.TextCol;
            float dot = Mathf.Max(3f, u * 2.4f);
            // 上节点
            Widgets.DrawBoxSolid(new Rect(r.x + 4f * u, r.y, dot, dot), c);
            // 两条斜线（3 段阶梯，上窄下宽）
            float y0 = r.y + dot;
            float y1 = r.yMax - dot;
            for (int k = 0; k < 3; k++)
            {
                float t01 = (k + 0.5f) / 3f;
                float yy = y0 + t01 * Mathf.Max(1f, y1 - y0 - 1f);
                float lx = r.x + (4.6f - t01 * 4.2f) * u;
                float rx = r.x + (5.4f + t01 * 4.2f) * u;
                Widgets.DrawBoxSolid(new Rect(lx, yy, Mathf.Max(1f, u * 0.9f), 1f), c);
                Widgets.DrawBoxSolid(new Rect(rx, yy, Mathf.Max(1f, u * 0.9f), 1f), c);
            }
            // 下两个节点（分开，不粘连）
            Widgets.DrawBoxSolid(new Rect(r.x, y1, dot, dot), c);
            Widgets.DrawBoxSolid(new Rect(r.xMax - dot, y1, dot, dot), c);
        }
        /// <summary>
        /// ★ 手绘"展开箭头"▼（提示标题可点击切换代理）。
        /// <para>为什么不用字符：SimSun 7pt 下 ▼ 可能缺字变方块，直接画像素最稳。</para>
        /// </summary>
        private static void ProxyArrow(Rect r)
        {
            Color c = AE2Draw.TextHi;
            float h = Mathf.Max(3f, r.height * 0.62f);
            float top = r.y + (r.height - h) * 0.5f;
            int rows = Mathf.Max(2, Mathf.RoundToInt(h));
            for (int k = 0; k < rows; k++)
            {
                float t01 = k / (float)rows;
                float w = Mathf.Max(1f, r.width * (1f - t01));
                Widgets.DrawBoxSolid(new Rect(r.x + (r.width - w) * 0.5f, top + k, w, 1f), c);
            }
        }
        /// <summary>工具行：左侧过滤方按钮（带计数）+ 右侧动作方按钮。全部 30×30 方形，AE2 原版风格。</summary>
        private float DrawToolRow(float x0, float y, float w, IList<CraftPlan> plans)
        {
            const float bh = 24f;      // 按钮高（模板实测 24）
            const float gap = 6f;   // ★ 按钮间距 6px（模板实测约 8）

            string[] labels = new string[6];
            labels[F_All] = "DS_CA_Orders".Translate().ToString();
            labels[F_Running] = "DS_CA_Working".Translate().ToString();
            labels[F_Waiting] = "DS_CA_Waiting".Translate().ToString();
            labels[F_Blocked] = "DS_AE2_Blocked".Translate().ToString();
            labels[F_Suspended] = "DS_BA_Suspended".Translate().ToString();
            labels[F_Done] = "DS_BA_PlanFinished".Translate().ToString();

            // ── 右侧动作方按钮（图标 + 文字，照模板：约 46×24，右边界留 6px）──
            const float actW = 56f;   // ★ 46→56：原来文字只余 1px，贴边像溢出
            // ★ 图标照目标图：+ 添加 / ;; 树（层级结构）/ [] 清。纯 ASCII（SimSun 无 ⚙ 等符号）
            string[] actIcon = new string[] { "+", "", "[]" };   // ★ 索引 1（树）改手绘 TreeGlyph
            string[] actText = new string[] { "添加", "树", "删树" };   // ★ 删"选中订单所在的那棵树"（不是清空全部）
            // ⚠️ 必须与 actIcon/actText **等长**：删掉"核心"按钮时漏改这里 ⇒
            //    "清"的 tooltip 显示成"存储核心"（实测踩过）
            string[] actTip = new string[]
            {
                "DS_CA_AddRecipe".Translate().ToString(),
                "DS_AE2_Tree_Btn".Translate().ToString(),
                "DS_CA_Delete".Translate().ToString(),   // ★ 单条删除（不是整树）—— 整树是右上角那个按钮
            };
            float ax = x0 + w - 6f - 3f * actW - 2f * gap;
            for (int i = 0; i < 3; i++)
            {
                Rect b = new Rect(ax + i * (actW + gap), y, actW, bh);
                if (AE2Draw.AE2TextButton(b, "", false)) DoAction(i);
if (i == 1) TreeGlyph(new Rect(b.x + 4f, b.y + 4f, 15f, 15f));   // ★ 15×15：13px 下节点糊成一团   // ★ 树：手绘结构图（照目标图）
                else AE2Draw.TextIn(new Rect(b.x + 3f, b.y + 1f, 17f, bh - 2f), actIcon[i], 0f, AE2Draw.TextCol, TextAnchor.MiddleLeft);
                AE2Draw.TextIn(new Rect(b.x + 21f, b.y + 1f, actW - 24f, bh - 2f), actText[i], 0f, AE2Draw.TextCol, TextAnchor.MiddleLeft);
                if (Mouse.IsOver(b)) TooltipHandler.TipRegion(b, actTip[i]);
            }

            // ── 左侧过滤按钮（**文字**，照模板：约 74×24，最多 6 个）──
            float availW = ax - gap - x0;
            float fw = Mathf.Clamp((availW - 5f * gap) / 6f, 40f, 74f);
            float bx = x0;
            for (int i = 0; i < 6; i++)
            {
                if (bx + fw > ax - gap) break;
                Rect b = new Rect(bx, y, fw, bh);
                if (AE2Draw.AE2TextButton(b, labels[i], filter == i)) filter = i;
                if (Mouse.IsOver(b))
                    TooltipHandler.TipRegion(b, (string)(labels[i] + "：" + FilterHint(i)));
                bx += fw + gap;
            }
            return y + bh + 4f;
        }

        /// <summary>右侧四个动作方按钮的实现。</summary>
        private void DoAction(int i)
        {
            switch (i)
            {
                case 0:   // 添加配方
                    Find.WindowStack.Add(new Dialog_AE2AddRecipe(comp));
                    break;
                case 1:   // 合成树
                    if (sel != null && sel.recipe != null)
                    {
                        CraftJob live = comp.JobForRecipe(sel.recipe);
                        if (live != null) Find.WindowStack.Add(new Window_AE2CraftTreeGraph(comp, live));
                        else
                        {
                            int want = (sel.mode == CraftPlan.ModeTarget) ? Mathf.Max(1, sel.targetCount)
                                     : ((sel.mode == CraftPlan.ModeCount) ? Mathf.Max(1, sel.remaining) : 1);
                            Find.WindowStack.Add(new Window_AE2CraftTreeGraph(comp, sel.recipe, sel.recipe.ProducedThingDef, want));
                        }
                    }
                    break;
                case 2:   // ★ 删除"选中订单所在的那棵合成树"（玩家自己加的独立订单不受影响）
                {
                    if (sel == null)
                    {
                        Messages.Message("DS_CA_DeleteTreeNoSel".Translate(),
                            MessageTypeDefOf.RejectInput, false);
                        break;
                    }
                    // ★ 用户要求：删树**不要二次确认**（要删就删）
                    comp.RemoveSelectedTree(sel);
                    sel = null;
                    break;
                }
            }
        }

        // ---- 顶栏：开关 / 超频 / 添加配方 + 耗电与统计 ----

        /// <summary>把一个订单归到六类之一（原版统计栏的语义）。</summary>
        private static int Classify(CraftPlan p)
        {
            if (p == null) return F_Waiting;
            if (p.Done || (p.mode == CraftPlan.ModeCount && p.remaining <= 0)) return F_Done;
            if (p.suspended) return F_Suspended;
            bool busy = false, blocked = false;
            for (int i = 0; i < p.lines.Count; i++)
            {
                CraftLine line = p.lines[i];
                if (line == null) continue;
                if (line.HasWork) busy = true;
                if (!string.IsNullOrEmpty(line.BlockKey)) blocked = true;
            }
            if (busy) return F_Running;
            if (blocked) return F_Blocked;
            return F_Waiting;
        }

        // =================================================================================
        // 左栏：核心实物（点取物）
        // =================================================================================
        private void DrawCore(Rect area, Map map)
        {
            AE2Draw.FlatColumn(area);
            // ★ 标题画在**头带里**（模板结构）；列表从容内容区开始（让开 8px 头带）
            AE2Draw.TextIn(new Rect(area.x + 5f, area.y, area.width - 10f, 24f),
                "DS_AE2_CoreItems".Translate().ToString() + "　·　" + CoreSummary(map), 0f,
                AE2Draw.TextCol, TextAnchor.MiddleLeft);
            Rect li = new Rect(area.x + 5f, area.y + 26f, area.width - 10f, area.height - 30f);

            Rect list = new Rect(li.x, li.y, li.width, li.yMax - li.y);   // ★ 去掉残留的 16px 搜索框偏移

            List<KeyValuePair<ThingDef, int>> items = CoreItems(map);
                // ★ 类别读取修正：配方的原料常写成"类别"（如肉类）⇒ 把核心实际库存中符合该类别的物品并进候选，                 //   否则只用 AllowedThingDefs 会漏掉鹿肉/猪肉等类别成员，导致"核心 0"而永不开工。                 for (int ci2 = 0; ci2 < it.Count; ci2++)                 {                     ThingDef cd = it[ci2].Key;                     if (cd != null && ic.filter.Allows(cd) && !allowed.Contains(cd)) allowed.Add(cd);                 }
            // ★ 搜索过滤（空 = 全部）
            if (!string.IsNullOrEmpty(searchCore))
            {
                List<KeyValuePair<ThingDef, int>> filtered = new List<KeyValuePair<ThingDef, int>>();
                for (int k = 0; k < items.Count; k++)
                {
                    string lb = items[k].Key.LabelCap.ToString();
                    if (lb.IndexOf(searchCore, StringComparison.OrdinalIgnoreCase) >= 0) filtered.Add(items[k]);
                }
                items = filtered;
            }
            int vis = Mathf.Max(1, Mathf.FloorToInt(list.height / (CoreRowH + 1f)));
            int maxStart = Mathf.Max(0, items.Count - vis);
            int start = Mathf.RoundToInt(scrollCore * maxStart);
            int drawn = Mathf.Min(vis, Mathf.Max(0, items.Count - start));

            for (int i = 0; i < drawn; i++)
            {
                int idx = start + i;
                Rect row = new Rect(list.x, list.y + i * (CoreRowH + 1f), list.width - 14f, CoreRowH);
                bool hover = Mouse.IsOver(row);
                AE2Draw.AE2Button(row, false, hover);   // ★ 恢复按钮效果（顶高光+底阴影+左右描边）
                // ★ 图标与文字**垂直居中**（原来写死 +1/+3 ⇒ 行变高后偏下）
                const float ICON = 18f;
                Rect ic = new Rect(row.x + 3f, row.y + (row.height - ICON) * 0.5f, ICON, ICON);
                AE2Draw.SlotBox(ic, false);
                Widgets.DefIcon(ic.ContractedBy(1f), items[idx].Key);
                AE2Draw.TextIn(new Rect(row.x + 25f, row.y, row.width - 64f, row.height),
                    items[idx].Key.LabelCap.ToString(), 1f, AE2Draw.TextCol, TextAnchor.MiddleLeft);
                AE2Draw.TextIn(new Rect(row.xMax - 46f, row.y, 42f, row.height), "×" + items[idx].Value, 1f, AE2Draw.TextDimCol, TextAnchor.MiddleRight);
                if (hover)
                {
                    TooltipHandler.TipRegion(row, (string)(items[idx].Key.LabelCap + " ×" + items[idx].Value
                        + "\n" + "DS_AE2_TakeHint".Translate().ToString()));
                    if (Widgets.ButtonInvisible(row))
                    {
                        // ★ 用户要求：点一下不再直接取 1 件，而是弹窗问"要取多少"
                        ThingDef __def = items[idx].Key;
                        int __have = items[idx].Value;
                        Find.WindowStack.Add(new Dialog_WithdrawAmount(__def.LabelCap.ToString(), __have, 0,
                            delegate (int n) { TakeFromCore(map, __def, n); }));
                        break;
                    }
                }
            }
            if (items.Count == 0)
            {
                AE2Draw.TextIn(new Rect(list.x + 2f, list.y + 2f, list.width - 4f, 16f), "DS_AE2_CoreEmpty".Translate().ToString(), 1f, AE2Draw.TextDimCol, TextAnchor.MiddleLeft);
            }
            if (items.Count > vis)   // C8：不够一屏不画滚动条
            AE2Draw.DragBar(new Rect(list.xMax - 12f, list.y, 12f, list.height), ref scrollCore,
                (items.Count <= 0) ? 1f : Mathf.Clamp01((float)vis / items.Count));
            AE2Draw.WheelScroll(list, ref scrollCore, maxStart);   // ★ 一格 = 一行   // ★ 滚轮在列表里也能滚
        }

        private static List<KeyValuePair<ThingDef, int>> CoreItems(Map map)
        {
            Dictionary<ThingDef, int> dict = new Dictionary<ThingDef, int>();
            List<Building_StorageCore> cores = CoreFinder.AllUsableCores(map);
            if (cores != null)
            {
                for (int i = 0; i < cores.Count; i++)
                {
                    Building_StorageCore core = cores[i];
                    if (core == null) continue;
                    ThingOwner held = core.GetDirectlyHeldThings();
                    if (held == null) continue;
                    for (int k = 0; k < held.Count; k++)
                    {
                        Thing t = held[k];
                        if (t == null || t.def == null) continue;
                        int n; dict.TryGetValue(t.def, out n);
                        dict[t.def] = n + t.stackCount;
                    }
                }
            }
            List<KeyValuePair<ThingDef, int>> list = new List<KeyValuePair<ThingDef, int>>(dict);
            list.Sort(delegate (KeyValuePair<ThingDef, int> a, KeyValuePair<ThingDef, int> b)
            {
                if (a.Value != b.Value) return b.Value - a.Value;
                return string.Compare(a.Key.label, b.Key.label, StringComparison.Ordinal);
            });
            return list;
        }

        // =================================================================================
        // 中栏：订单列表（按筛选）
        // =================================================================================
        private void DrawOrders(Rect area, IList<CraftPlan> plans)
        {
            AE2Draw.FlatColumn(area);
            AE2Draw.TextIn(new Rect(area.x + 5f, area.y, area.width - 10f, 24f),
                "DS_CA_Orders".Translate().ToString(), 0f, AE2Draw.TextCol, TextAnchor.MiddleLeft);
            Rect li = new Rect(area.x + 5f, area.y + 26f, area.width - 10f, area.height - 30f);

            List<CraftPlan> shown = new List<CraftPlan>();
            for (int i = 0; i < plans.Count; i++)
            {
                if (plans[i] == null) continue;
                if (filter == F_All || Classify(plans[i]) == filter) shown.Add(plans[i]);
            }

            Rect list = new Rect(li.x, li.y, li.width, li.height);
            if (shown.Count == 0)
            {
                AE2Draw.TextIn(new Rect(list.x + 3f, list.y + 3f, list.width - 6f, 16f), (plans.Count == 0) ? "DS_CA_PickOrder".Translate().ToString() : "DS_CA_NoOrder".Translate().ToString(), 1f, AE2Draw.TextDimCol, TextAnchor.MiddleLeft);
                return;
            }

            int vis = Mathf.Max(1, Mathf.FloorToInt(list.height / (RowH + 1f)));
            int maxStart = Mathf.Max(0, shown.Count - vis);
            int start = Mathf.RoundToInt(scrollOrders * maxStart);
            int drawn = Mathf.Min(vis, Mathf.Max(0, shown.Count - start));

            for (int i = 0; i < drawn; i++)
            {
                CraftPlan p = shown[start + i];
                if (p == null || p.recipe == null) continue;
                Rect row = new Rect(list.x, list.y + i * (RowH + 1f), list.width - 14f, RowH);
                bool isSel = (p == sel);
                AE2Draw.AE2Button(row, isSel, Mouse.IsOver(row));   // ★ 恢复按钮效果；选中用浅面

                // ★ 图标与文字**垂直居中**（原来写死 +3/+2/+4 ⇒ 行高 40 时偏上）
                const float ICON2 = 18f;
                Rect ic = new Rect(row.x + 3f, row.y + (row.height - ICON2) * 0.5f, ICON2, ICON2);
                AE2Draw.SlotBox(ic, false);
                Widgets.DefIcon(ic.ContractedBy(1f), p.recipe.ProducedThingDef);

                Color tc = isSel ? AE2Draw.TextHi : AE2Draw.TextCol;
                CraftPlan __p2 = plans[i];                 bool __sub2 = (__p2 != null && __p2.recipe != null && comp.IsSubordinateStep(__p2.recipe));
                if (__sub2) AE2Draw.TextIn(new Rect(row.x + 23f, row.y, 16f, row.height), "└", 1f, AE2Draw.TextDimCol, TextAnchor.MiddleLeft);
                AE2Draw.TextIn(new Rect(row.x + (__sub2 ? 41f : 25f), row.y, row.width - (__sub2 ? 100f : 84f), row.height),
                    p.recipe.ProducedThingDef.LabelCap.ToString(), 1f, tc, TextAnchor.MiddleLeft);
                // ★ 数量文字在左、进度条在右（原来两者 x 区间重叠 ⇒ 条压住数字）
                AE2Draw.TextIn(new Rect(row.xMax - 108f, row.y, 46f, row.height), CountLabel(p), 1f,
                    isSel ? AE2Draw.TextHi : AE2Draw.TextDimCol, TextAnchor.MiddleRight);

                if (Mouse.IsOver(row)) TooltipHandler.TipRegion(row, (string)(p.recipe.LabelCap + "\n" + StatusText(p)));
                // D11：行内进度条（正在做的那一件）—— 靠右贴齐，不与数量文字重叠
                CraftLine __fl = (p.lines.Count > 0) ? p.lines[0] : null;
                float __pct = (__fl != null && __fl.HasWork) ? __fl.Progress01 : (p.Done ? 1f : 0f);
                AE2Draw.Bar(new Rect(row.xMax - 58f, row.y + (row.height - 5f) * 0.5f, 46f, 5f), __pct, isSel);   // ★ 条在右端

                // D10：右键菜单（暂停 / +10 / 改为无限 / 删除）
                if (Mouse.IsOver(row) && Event.current != null && Event.current.type == EventType.MouseDown && Event.current.button == 1)
                {
                    Event.current.Use();
                    List<FloatMenuOption> mo = new List<FloatMenuOption>();
                    CraftPlan cap = p;
                    mo.Add(new FloatMenuOption(cap.suspended ? "DS_BA_Resume".Translate().ToString() : "DS_BA_Suspend".Translate().ToString(), delegate { cap.suspended = !cap.suspended; }));
                    mo.Add(new FloatMenuOption("+10", delegate { cap.AddCount(10); comp.NotifyRootCountChanged(cap); }));
                    mo.Add(new FloatMenuOption("DS_CA_SetForever".Translate().ToString(), delegate { cap.SetForever(); }));
                    mo.Add(new FloatMenuOption("DS_CA_Delete".Translate().ToString(), delegate { comp.RemovePlan(cap); sel = null; }));
                    Find.WindowStack.Add(new FloatMenu(mo));
                }
                if (Widgets.ButtonInvisible(row)) { sel = p; countBuf = null; }
            }

            if (shown.Count > vis)   // C8：不够一屏不画滚动条
            AE2Draw.DragBar(new Rect(list.xMax - 12f, list.y, 12f, list.height), ref scrollOrders,
                (shown.Count <= 0) ? 1f : Mathf.Clamp01((float)vis / shown.Count));
            AE2Draw.WheelScroll(list, ref scrollOrders, maxStart);   // ★ 一格 = 一行   // ★ 滚轮在列表里也能滚
        }

        private static string CountLabel(CraftPlan p)
        {
            if (p == null) return "";
            if (p.mode == CraftPlan.ModeForever) return "∞";
            if (p.mode == CraftPlan.ModeCount) return "×" + Mathf.Max(0, p.remaining);
            return p.countedCount + "/" + p.targetCount;
        }

        private static string StatusText(CraftPlan p)
        {
            if (p == null) return "";
            if (p.Done) return "DS_BA_PlanFinished".Translate().ToString();
            if (p.suspended) return "DS_BA_Suspended".Translate().ToString();
            if (p.mode == CraftPlan.ModeTarget && !p.wantsWork)
                return "DS_CA_TargetPaused".Translate(p.countedCount, p.targetCount).ToString();
            // ★ 目标④ 补齐（原版同款）：一条产线都没占上、维持模式又没达标 ⇒ 那句「范围内没有能做工的工作台」
            if (p.lines.Count == 0 && p.mode == CraftPlan.ModeTarget && p.countedCount < p.targetCount)
                return "DS_BA_NoBench".Translate().ToString();
            for (int i = 0; i < p.lines.Count; i++)
            {
                CraftLine line = p.lines[i];
                if (line == null) continue;
                if (line.HasWork) return "DS_CA_Working".Translate().ToString();
                if (!string.IsNullOrEmpty(line.BlockKey)) return line.BlockKey.Translate().ToString();
            }
            return "DS_CA_Waiting".Translate().ToString();
        }

        // =================================================================================
        // 右栏：详情 + 原版全套按钮 + 改制作材料
        // =================================================================================
        private void DrawDetail(Rect area, Map map)
        {
            AE2Draw.FlatColumn(area);
            AE2Draw.TextIn(new Rect(area.x + 5f, area.y, area.width - 10f, 24f),
                "DS_CA_Detail".Translate().ToString(), 0f, AE2Draw.TextCol, TextAnchor.MiddleLeft);
            Rect ri = new Rect(area.x + 5f, area.y + 26f, area.width - 10f, area.height - 30f);
            float y = ri.y;

            if (sel == null || sel.recipe == null)
            {
                AE2Draw.TextIn(new Rect(ri.x + 3f, ri.y + 3f, ri.width - 6f, 18f),
                    "DS_CA_PickOrder".Translate().ToString(), 1f, AE2Draw.TextDimCol, TextAnchor.MiddleLeft);
                return;
            }

            CraftPlan p = sel;
            // ★ 只读缓存，**绝不在这里扫描** —— 本方法是每帧重绘调用的，
            //   原来在这里调 RefreshTargetStateIfStale ⇒ 每一帧都遍历所有核心数一遍产物。
            //   刷新时机改为：窗口打开时一次（见 Window_AE2CraftPanel 构造函数）
            //   + 每次产出时一次（BillCraftFunnel ⇒ comp.RecountNow）+ 调度侧 30 tick 兜底。
            CraftLine first = (p.lines.Count > 0) ? p.lines[0] : null;

            // ============================================================================
            //  ★ 详情栏：严格按目标图结构（4 倍放大图实测）
            //     头（AE2 按钮 26px + 槽位 + 名称）
            //     → 5 行键值（标签 #878FA5 缩进 3px，值 #413F54 起 x+39，行距 19）
            //     → 进度条（青 + 右侧百分比）
            //     → 合成存储行 + 橙色进度条
            //     → 按钮 4 行（32px / 间隔 4）
            // ============================================================================
            const float LBL_X = 3f;      // 标签左缩进（目标实测）
            const float VAL_X = 68f;   // ★ 55→68：标签最宽到"技能要求"（4 字 ≈ 52px），55 会贴住值     // ★ 值起点（SimSun 7pt 下"技能要求"实测 41.6px ⇒ 39 会裁切，改 55）
            const float ROW_H = 19f;     // 行距（目标实测）

            // ── 详情头：AE2 按钮 + 槽位 + 产物名 ──
            Rect head = new Rect(ri.x, y, ri.width, 26f);
            AE2Draw.AE2Button(head, false, false);
            Rect hi = new Rect(head.x + 4f, head.y + 4f, 18f, 18f);
            AE2Draw.SlotBox(hi, false);
            Widgets.DefIcon(hi.ContractedBy(1f), p.recipe.ProducedThingDef);
            AE2Draw.TextIn(new Rect(head.x + 26f, head.y, head.width - 30f, head.height),
                p.recipe.ProducedThingDef.LabelCap + "  " + CountLabel(p), 1f, AE2Draw.TextCol, TextAnchor.MiddleLeft);
            y += 30f;

            // ── 行1：技能要求 ──
            AE2Draw.TextIn(new Rect(ri.x + LBL_X, y, VAL_X - LBL_X, ROW_H), "DS_CA_Skill".Translate().ToString(), 0f, AE2Draw.TextDimCol, TextAnchor.MiddleLeft);
            AE2Draw.TextIn(new Rect(ri.x + VAL_X, y, ri.width - VAL_X - 2f, ROW_H), SkillText(p.recipe), 0f, AE2Draw.TextCol, TextAnchor.MiddleLeft);
            y += ROW_H;

            // ── 行2：材料（产物所需 + 核心存量）──
            AE2Draw.TextIn(new Rect(ri.x + LBL_X, y, VAL_X - LBL_X, ROW_H), "DS_CA_Material".Translate().ToString(), 0f, AE2Draw.TextDimCol, TextAnchor.MiddleLeft);
            AE2Draw.TextIn(new Rect(ri.x + VAL_X, y, ri.width - VAL_X - 2f, ROW_H),
                MaterialsSummary(p, comp.parent.Map), 0f, AE2Draw.TextCol, TextAnchor.MiddleLeft);
            y += ROW_H;

            // ── 行3：进度（百分比）──
            float pct = (first != null && first.HasWork) ? first.Progress01 : (p.Done ? 1f : 0f);
            AE2Draw.TextIn(new Rect(ri.x + LBL_X, y, VAL_X - LBL_X, ROW_H), "DS_CA_Progress".Translate().ToString(), 0f, AE2Draw.TextDimCol, TextAnchor.MiddleLeft);
            AE2Draw.TextIn(new Rect(ri.x + VAL_X, y, ri.width - VAL_X - 2f, ROW_H), ((int)(pct * 100f)) + "%", 0f, AE2Draw.TextCol, TextAnchor.MiddleLeft);
            y += ROW_H;

            // ── 行4：状态 ──
            int cls = Classify(p);
            string st = StatusText(p);
            if (p.lines.Count == 0 && p.mode == CraftPlan.ModeTarget && p.countedCount < p.targetCount) cls = F_Blocked;
            Color stCol = (cls == F_Done) ? AE2Draw.Accent
                : ((cls == F_Suspended) ? AE2Draw.TextDimCol
                : ((cls == F_Blocked) ? AE2Draw.Bad : ((cls == F_Running) ? AE2Draw.Accent : AE2Draw.TextCol)));
            AE2Draw.TextIn(new Rect(ri.x + LBL_X, y, VAL_X - LBL_X, ROW_H), "DS_CA_Status".Translate().ToString(), 0f, AE2Draw.TextDimCol, TextAnchor.MiddleLeft);
            AE2Draw.TextIn(new Rect(ri.x + VAL_X, y, ri.width - VAL_X - 2f, ROW_H), st, 0f, stCol, TextAnchor.MiddleLeft);
            y += ROW_H;

            // ── 行5：工作台 ──
            AE2Draw.TextIn(new Rect(ri.x + LBL_X, y, VAL_X - LBL_X, ROW_H), "DS_CA_Bench".Translate().ToString(), 0f, AE2Draw.TextDimCol, TextAnchor.MiddleLeft);
            AE2Draw.TextIn(new Rect(ri.x + VAL_X, y, ri.width - VAL_X - 2f, ROW_H),
                "DS_BA_Lines".Translate(BusyLines(p), comp.UsableBenchCountFor(p.recipe)).ToString(), 0f, AE2Draw.TextCol, TextAnchor.MiddleLeft);
            y += ROW_H + 6f;

            // ── 进度条（青）+ 右侧百分比 ──
            AE2Draw.StateBar(new Rect(ri.x + 2f, y, ri.width - 6f, 16f), pct, AE2Draw.Accent, ((int)(pct * 100f)) + "%");   // ★ AE2 同款条
            y += 22f;

            // ── 合成存储行 + 橙色条 ──
            AE2Draw.TextIn(new Rect(ri.x + LBL_X, y, ri.width - LBL_X - 2f, ROW_H), "DS_CA_CoreStore".Translate().ToString() + "  " + CoreSummary(comp.parent.Map), 0f, AE2Draw.TextDimCol, TextAnchor.MiddleLeft);
            y += ROW_H + 2f;
            // ★ 容量条：接真实 used/cap（与存储页签同一口径）
            float corePct = 0f;
            string coreTxt = null;
            Building_StorageCore c0 = FirstCore(comp.parent.Map);
            if (c0 != null)
            {
                int cap0 = c0.maxStacks;
                int used0 = (c0.innerContainer != null) ? c0.innerContainer.Count : 0;
                corePct = (cap0 > 0) ? Mathf.Clamp01((float)used0 / cap0) : 0f;
                coreTxt = (int)(corePct * 100f) + "%";
            }
            AE2Draw.StateBar(new Rect(ri.x + 2f, y, ri.width - 6f, 14f), corePct, AE2Draw.Warn, coreTxt);
            y += 20f;
            // ============================================================================
            //  ★ 按第一张模板图重构：按钮**从内容往下排**（模板实测 y113 起），不钉栏底
            //     模板间距：按钮高 32、行间 4 → 见 y113..144 / 149..180 / 185..216 / 221..252
            // ============================================================================
            float bh = 32f;                       // 按钮高（模板实测 32px）
            const float bgap = 4f;                // 行间（模板实测 4px）
            y += 4f;                              // 与上方内容留 4px
            float x = ri.x;

            bool canTarget = comp.CanTargetMode(p);
            int modeIdx = (p.mode == CraftPlan.ModeTarget) ? 0 : ((p.mode == CraftPlan.ModeCount) ? 1 : 2);
            string[] modeLabels = new string[]
            {
                "DS_CA_ModeKeepShort".Translate().ToString(),
                "DS_CA_ModeCountShort".Translate().ToString(),
                "DS_CA_ModeForeverShort".Translate().ToString(),
            };
            float modeRowW = Mathf.Min(ri.width, 3f * 58f + 2f * bgap);
            int hitMode = AE2Draw.Segmented(new Rect(x, y, modeRowW, bh), modeLabels, modeIdx, bgap);
            if (hitMode == 0)
            {
                if (canTarget)
                {
                    p.SetTarget(p.targetCount > 0 ? p.targetCount : 10);
                    // ★ 切到"维持"时顺手打开「达标即暂停」：否则维持模式会一直做下去（实测踩过）
                    if (!p.pauseWhenSatisfied) p.pauseWhenSatisfied = true;
                }
            }
            else if (hitMode == 1)
            {
                if (p.mode != CraftPlan.ModeCount)
                {
                    p.mode = CraftPlan.ModeCount;
                    p.remaining = 0;
                    p.AddCount(1); comp.NotifyRootCountChanged(p);
                }
            }
            else if (hitMode == 2)
            {
                p.SetForever();
            }
            y += bh + bgap;

            // ---- 第二行：数量步进 −10 −1 [输入] +1 +10 · 暂停 ----
            x = ri.x;
            if (AE2Draw.TextButton(new Rect(x, y, 42f, bh), "−10")) { p.AddCount(-10); comp.NotifyRootCountChanged(p); }
            x += 46f;
            if (AE2Draw.TextButton(new Rect(x, y, 36f, bh), "−1")) { p.AddCount(-1); comp.NotifyRootCountChanged(p); }
            x += 40f;

            Rect countR = new Rect(x, y, 58f, bh);
            // ★ 槽里的值 = 这条订单**当前**的数量。原来是"换订单时才刷新缓冲"，
            //   于是点 −1/+1/+10 之后槽里还是旧数字（实机反馈："加减数量时候下面那个槽的数量怎么不变"）。
            int curShown = (p.mode == CraftPlan.ModeTarget) ? p.targetCount : Mathf.Max(0, p.remaining);
            if (countBufPlan != p || countBufShown != curShown)
            {
                countBufPlan = p;
                countBufShown = curShown;
                countBuf = curShown.ToString();
            }
            AE2Draw.Sunken(countR, AE2Draw.Slot);
            string typed = Widgets.TextField(countR.ContractedBy(3f), countBuf ?? "");
            if (typed != countBuf)
            {
                countBuf = typed;
                int v;
                if (int.TryParse(typed, out v) && v >= 0)
                {
                    if (p.mode == CraftPlan.ModeTarget) p.SetTarget(Mathf.Max(1, v));
                    else p.remaining = v;
                    countBufShown = (p.mode == CraftPlan.ModeTarget) ? p.targetCount : Mathf.Max(0, p.remaining);
                    comp.NotifyRootCountChanged(p);   // ★ 手打数字也要让前置产物需求跟着变
                }
            }
            x += 62f;
            if (AE2Draw.TextButton(new Rect(x, y, 36f, bh), "+1")) { p.AddCount(1); comp.NotifyRootCountChanged(p); }
            x += 40f;
            if (AE2Draw.TextButton(new Rect(x, y, 42f, bh), "+10")) { p.AddCount(10); comp.NotifyRootCountChanged(p); }
            x += 46f;
            string susLabel = p.suspended ? "DS_BA_Resume".Translate().ToString() : "DS_BA_Suspend".Translate().ToString();
            if (AE2Draw.TextButton(new Rect(x, y, 66f, bh), susLabel)) p.suspended = !p.suspended;
            y += bh + bgap;   // ★ 行间 4px（模板实测）

            // ---- 第三行：排序 / 暂停达标 / 改材料 / 改风格 / 删除 ----
            //  ★ 按**内容宽度**自适应 + 放不下自动换行。
            //    原来写死累加（56+60+60+78+124+114+124 = 514px）⇒ 详情栏只有
            //    ri.width 宽，必然溢出右边界（实测踩过）。
            // ★ 顺序**钉死**：上移 / 下移 / 达标即暂停 / 材料 / 风格 / 删除
            //   （用户口径：材料在左、风格在中、删除在最右）
            x = ri.x;
            string[] row3 =
            {
                "DS_CA_Up".Translate().ToString(),
                "DS_CA_Down".Translate().ToString(),
                "DS_CA_PauseWhenSatisfied".Translate().ToString(),
                "DS_AE2_Stuff".Translate(StuffName(p)).ToString(),
                "DS_AE2_Style".Translate(StyleName(p)).ToString(),
                "DS_CA_Delete".Translate().ToString(),   // ★ 单条删除（不是整树）—— 整树是右上角那个按钮
            };
            // 这一行按钮多，单独用更紧的间距（2px）和内边距（14px），尽量排在一行里，
            // 避免"删除"被挤到上一行 ⇒ 玩家看到顺序错乱（实机反馈）。
            const float row3Gap = 2f;
            const float row3Pad = 14f;
            for (int bi = 0; bi < row3.Length; bi++)
            {
                float bw = Mathf.Ceil(Verse.Text.CalcSize(row3[bi]).x) + row3Pad;
                if (bw > ri.width) bw = ri.width;
                if (x > ri.x && x + bw > ri.xMax)       // 放不下 ⇒ 换行
                {
                    x = ri.x;
                    y += bh + bgap;
                }
                bool on = (bi == 2) ? p.pauseWhenSatisfied
                        : ((bi == 3) ? (p.allowedStuff != null)
                        : ((bi == 4) ? (p.styleDef != null) : false));
                if (AE2Draw.TextButton(new Rect(x, y, bw, bh), row3[bi], on))
                {
                    switch (bi)
                    {
                        case 0: comp.MovePlan(p, -1); break;
                        case 1: comp.MovePlan(p, 1); break;
                        case 2: p.pauseWhenSatisfied = !p.pauseWhenSatisfied; break;
                        case 3: OpenStuffMenu(p); break;
                        case 4: OpenStyleMenu(p); break;
                        case 5: comp.RemovePlan(p, true); sel = null; break;   // ★ 只删这一条（不是整树）
                    }
                }
                x += bw + row3Gap;
            }
        }

        /// <summary>★ 目标④：给"没指定代理"的入口（底栏主按钮）自动挑一台制作代理。</summary>
        private static CompBillAutomation FindAnyProxy()
        {
            Map map = Find.CurrentMap;
            if (map == null) return null;
            List<Thing> all = map.listerThings.AllThings;
            for (int i = 0; i < all.Count; i++)
            {
                Thing t = all[i];
                if (t == null || !t.Spawned) continue;
                CompBillAutomation c = t.TryGetComp<CompBillAutomation>();
                if (c != null) return c;
            }
            return null;
        }

        /// <summary>本件还剩多少秒（公式与原版面板 RemainSeconds 完全一致）。</summary>
        private float RemainSeconds(CraftLine line)
        {
            float rate = Math.Max(0.0001f, line.BaseRate);
            if (comp != null) rate *= Math.Max(0.01f, comp.Props.workSpeedMult) * comp.OverclockSpeedMult;
            return Math.Max(0f, line.WorkLeft) / rate / 60f;
        }

        private static int BusyLines(CraftPlan p)
        {
            int n = 0;
            for (int i = 0; i < p.lines.Count; i++)
            {
                CraftLine l = p.lines[i];
                if (l != null && l.HasWork) n++;
            }
            return n;
        }

        private static string ModeShort(CraftPlan p)
        {
            if (p.mode == CraftPlan.ModeForever) return "DS_BA_Forever".Translate().ToString();
            if (p.mode == CraftPlan.ModeCount) return "DS_BA_Remain".Translate(Mathf.Max(0, p.remaining)).ToString();
            return p.countedCount + "/" + p.targetCount;
        }

        private static string ModeLabel(CraftPlan p)
        {
            if (p.mode == CraftPlan.ModeForever) return "DS_BA_Forever".Translate().ToString();
            if (p.mode == CraftPlan.ModeCount) return "DS_BA_Remain".Translate(Mathf.Max(0, p.remaining)).ToString();
            return "DS_CA_Target".Translate(p.countedCount, p.targetCount).ToString();
        }

        private static string SkillText(RecipeDef r)
        {
            if (r == null) return "?";
            if (r.skillRequirements != null && r.skillRequirements.Count > 0)
            {
                for (int i = 0; i < r.skillRequirements.Count; i++)
                {
                    SkillRequirement sr = r.skillRequirements[i];
                    if (sr == null || sr.skill == null) continue;
                    return sr.skill.LabelCap.ToString() + " " + sr.minLevel;
                }
            }
            // ★ 修复：没有技能要求的配方原来返回**工作量**（workAmount），
            //   于是界面上显示成"技能要求 140"（测试配方 workAmount=140）—— 拿错字段了。
            return "DS_CA_SkillNone".Translate().ToString();
        }

        private static string StuffName(CraftPlan p)
        {
            if (p == null || p.allowedStuff == null) return "DS_AE2_StuffAny".Translate().ToString();
            return p.allowedStuff.LabelCap.ToString();
        }

        /// <summary>当前风格名（null = 默认风格）。</summary>
        private static string StyleName(CraftPlan p)
        {
            if (p == null || p.styleDef == null) return "DS_AE2_StyleAny".Translate().ToString();
            return p.styleDef.LabelCap.ToString();
        }

        /// <summary>
        /// 改制作风格：列出游戏里所有 ThingStyleDef（默认 + 各文化风格）。
        /// 生效链：plan.styleDef ⇒ BillCraftFunnel 传 bill.style ⇒ 原版 GenRecipe.PostProcessProduct 落到产物上。
        /// </summary>
        private void OpenStyleMenu(CraftPlan p)
        {
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            opts.Add(new FloatMenuOption("DS_AE2_StyleAny".Translate().ToString(), delegate { p.styleDef = null; }));
            List<ThingStyleDef> all = DefDatabase<ThingStyleDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                ThingStyleDef sd = all[i];
                if (sd == null) continue;
                ThingStyleDef cap = sd;
                opts.Add(new FloatMenuOption(sd.LabelCap.ToString(), delegate { p.styleDef = cap; }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        /// <summary>改制作材料：不限 + 这个配方能吃的每种材料（A 组的 BillProbe.UsableStuffs）。</summary>
        private void OpenStuffMenu(CraftPlan p)
        {
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            opts.Add(new FloatMenuOption("DS_AE2_StuffAny".Translate().ToString(), delegate { p.allowedStuff = null; }));
            List<ThingDef> list = BillProbe.UsableStuffs(p.recipe);
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    ThingDef d = list[i];
                    if (d == null) continue;
                    ThingDef dd = d;
                    opts.Add(new FloatMenuOption(dd.LabelCap.ToString(), delegate { p.allowedStuff = dd; }));
                }
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        /// <summary>所需材料：优先用"正在做的那件"的真实配料（含核心库存、不足标红）；没有就退回配方的材料筛选。</summary>
        private static float DrawMaterials(CraftPlan p, CraftLine first, Rect ri, float y, Map map)
        {
            Dictionary<ThingDef, int> stock = CraftTree.CoreStock(map);
            if (first != null && first.HasWork && first.Ingredients != null && first.Counts != null)
            {
                for (int i = 0; i < first.Ingredients.Length && i < first.Counts.Length; i++)
                {
                    Thing t = first.Ingredients[i];
                    if (t == null || t.def == null) continue;
                    int need = first.Counts[i];
                    int have;
                    stock.TryGetValue(t.def, out have);
                    AE2Draw.TextIn(new Rect(ri.x + 4f, y, ri.width - 8f, 15f),
                        "DS_CA_Have".Translate(need + "×" + t.def.LabelCap, have).ToString(),
                        1f, (have < need) ? AE2Draw.Bad : AE2Draw.TextCol, TextAnchor.MiddleLeft);
            y += 19f;   // C6：行距放宽
                }
                return y;
            }

            if (p.recipe.ingredients == null || p.recipe.ingredients.Count == 0)
            {
                AE2Draw.TextIn(new Rect(ri.x + 4f, y, ri.width - 8f, 15f),
                    "DS_CA_NoMaterials".Translate().ToString(), 1f, AE2Draw.TextDimCol, TextAnchor.MiddleLeft);
                return y + 16f;
            }
            int __mult = (p.mode == CraftPlan.ModeTarget) ? Mathf.Max(1, p.targetCount - p.countedCount)
                       : ((p.mode == CraftPlan.ModeCount) ? Mathf.Max(1, p.remaining) : 1);   // B3：还差几件
            for (int i = 0; i < p.recipe.ingredients.Count; i++)
            {
                IngredientCount ic = p.recipe.ingredients[i];
                if (ic == null || ic.filter == null) continue;
                // ★ 类别读取修正：不再只看 filter.AllowedThingDefs（那只含配方里显式写出的物品），
                //   改为用 filter.Allows() 遍历**全部核心库存** ⇒ 类别成员（鹿肉/猪肉…）既进列表也计数。
                string names = "";
                int have = 0;
                int shown = 0;
                foreach (KeyValuePair<ThingDef, int> kv in stock)
                {
                    if (kv.Key == null || kv.Value <= 0) continue;
                    if (!ic.filter.Allows(kv.Key)) continue;
                    have += kv.Value;
                    if (shown < 3)
                    {
                        names += ((names.Length > 0) ? " / " : "") + kv.Key.LabelCap;
                        shown++;
                    }
                }
                if (shown == 0 && ic.filter.AllowedThingDefs != null)
                {
                    // 核心里一个都没有 ⇒ 退回显示配方显式列出的前几个（让玩家知道要什么）
                    foreach (ThingDef d0 in ic.filter.AllowedThingDefs)
                    {
                        if (d0 == null) continue;
                        names += ((names.Length > 0) ? " / " : "") + d0.LabelCap;
                        if (++shown >= 3) break;
                    }
                }
                int __need = 0;
                try { __need = (int)(ic.GetBaseCount() * __mult); } catch { __need = __mult; }
                AE2Draw.TextIn(new Rect(ri.x + 4f, y, ri.width - 8f, 15f),
                    "DS_AE2_NeedWithCore".Translate(names, __need, have).ToString(), 1f, (have >= __need) ? AE2Draw.TextCol : AE2Draw.Bad, TextAnchor.MiddleLeft);
                    y += 19f;   // ★ 每种材料往下走一行（之前这行被编辑吃掉 ⇒ 二层甲/二层乙叠在一起）
            }
            return y;
        }

        /// <summary>从核心取出若干件放到代理旁（放不下塞回核心，不丢东西）。</summary>
        /// <summary>C9：左栏标题摘要（多少种 / 多少件）。</summary>
        /// <summary>
        /// 详情栏"材料"行：**第一项材料名 + 需求量 + 核心存量**
        /// （照目标图 "钢铁 ×12  核心 1762"）。
        /// </summary>
        private static string MaterialsSummary(CraftPlan p, Map map)
        {
            if (p == null || p.recipe == null) return "-";
            RecipeDef r = p.recipe;
            string matName = "-";
            int need = 0;
            if (r.ingredients != null && r.ingredients.Count > 0 && r.ingredients[0].filter != null)
            {
                List<ThingDef> opts = r.ingredients[0].filter.AllowedThingDefs != null
                    ? new List<ThingDef>(r.ingredients[0].filter.AllowedThingDefs) : new List<ThingDef>();
                if (opts.Count > 0) matName = opts[0].LabelCap.ToString();
                try { need = (int)r.ingredients[0].GetBaseCount(); } catch { need = 1; }   // ★ 原版口径（含材质系数）
            }
            // 核心存量：直接查 CoreItems（已带类别过滤修正）
            int have = 0;
            List<KeyValuePair<ThingDef, int>> it = CoreItems(map);
            for (int k = 0; k < it.Count; k++)
            {
                if (it[k].Key == null) continue;
                if (r.ingredients != null && r.ingredients.Count > 0 && r.ingredients[0].filter != null
                    && r.ingredients[0].filter.Allows(it[k].Key))
                    have += it[k].Value;
            }
            return matName + " ×" + need + "  " + "DS_CA_CoreHave".Translate(have).ToString();
        }
        private static string CoreSummary(Map map)
        {
            List<KeyValuePair<ThingDef, int>> it = CoreItems(map);

            int total = 0;
            for (int k = 0; k < it.Count; k++) total += it[k].Value;
            return "DS_AE2_CoreSummary".Translate(it.Count, total).ToString();
        }

        /// <summary>D12：本图第一台数字存储核心（供"存储核心"快捷跳转）。</summary>
        /// <summary>
        /// ★ 全部暂停 / 全部开始：把本代理的**所有订单**统一挂起或恢复。
        /// <para>为什么有用：改材料、等供电、临时腾核心容量时，原来要一条条点挂起。
        /// 注意 <c>suspended</c> 与 <c>paused</c> 不同 —— 前者是"玩家挂起"，后者是
        /// "内核因缺料/断电自行暂停"，所以这里只动 <c>suspended</c>。</para>
        /// </summary>
        private void SetAllSuspended(bool on)
        {
            IList<CraftPlan> all = comp.PlansForReading;
            if (all == null) return;
            int hit = 0;
            for (int i = 0; i < all.Count; i++)
            {
                CraftPlan cp = all[i];
                if (cp == null) continue;
                if (cp.suspended != on) { cp.suspended = on; hit++; }
            }
            Messages.Message("DS_CA_SuspendAllMsg".Translate(hit, on ? "DS_BA_Suspend".Translate().ToString() : "DS_BA_Resume".Translate().ToString()),
                MessageTypeDefOf.SilentInput, false);
        }
        /// <summary>★ 点标题切换制作代理：列出本图所有带制作自动化的代理建筑。</summary>
        private void OpenProxyMenu()
        {
            Map map = comp.parent.Map;
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            List<Building> all = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < all.Count; i++)
            {
                Building b = all[i];
                if (b == null) continue;
                CompBillAutomation c = b.GetComp<CompBillAutomation>();
                if (c == null) continue;
                CompBillAutomation cc = c;
                opts.Add(new FloatMenuOption(b.LabelCap.ToString() + (b == comp.parent ? "  ✓" : ""), delegate { comp = cc; sel = null; }));
            }
            if (opts.Count == 0) opts.Add(new FloatMenuOption("DS_AE2_NoProxyInMap".Translate().ToString(), null));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private static Building_StorageCore FirstCore(Map map)
        {
            if (map == null) return null;
            List<Building_StorageCore> cores = CoreFinder.AllUsableCores(map);
            return (cores != null && cores.Count > 0) ? cores[0] : null;
        }

        /// <summary>B5：六个筛选按钮的悬停说明。</summary>
        private static string FilterHint(int i)
        {
            switch (i)
            {
                case F_All: return "DS_AE2_FilterAllHint".Translate().ToString();
                case F_Running: return "DS_AE2_FilterRunningHint".Translate().ToString();
                case F_Waiting: return "DS_AE2_FilterWaitingHint".Translate().ToString();
                case F_Blocked: return "DS_AE2_FilterBlockedHint".Translate().ToString();
                case F_Suspended: return "DS_AE2_FilterSuspendedHint".Translate().ToString();
                case F_Done: return "DS_AE2_FilterDoneHint".Translate().ToString();
                default: return "";
            }
        }

        private void TakeFromCore(Map map, ThingDef def, int count)
        {
            if (map == null || def == null || count <= 0) return;
            List<Building_StorageCore> cores = CoreFinder.AllUsableCores(map);
            if (cores == null) return;
            int left = count;
            IntVec3 cell = (comp != null && comp.parent != null) ? comp.parent.Position : map.Center;
            for (int i = 0; i < cores.Count && left > 0; i++)
            {
                Building_StorageCore core = cores[i];
                if (core == null) continue;
                ThingOwner held = core.GetDirectlyHeldThings();
                if (held == null) continue;
                for (int k = held.Count - 1; k >= 0 && left > 0; k--)
                {
                    Thing th = held[k];
                    if (th == null || th.def != def) continue;
                    int n = Mathf.Min(left, th.stackCount);
                    Thing part = th.SplitOff(n);
                    if (part == null) continue;
                    left -= n;
                    if (!GenPlace.TryPlaceThing(part, cell, map, ThingPlaceMode.Near))
                    {
                        if (!held.TryAdd(part, false)) GenPlace.TryPlaceThing(part, cell, map, ThingPlaceMode.Near);
                    }
                }
            }
        }
    }
}
