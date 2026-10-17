// =====================================================================================
//  【本地新增文件】Window_AE2CraftTreeGraph —— AE2「合成树预览」那种横向节点树
// -------------------------------------------------------------------------------------
//  用户给的参考（AE2 的合成树预览）：节点 = 物品格子（20×20 带图标），
//  用**折线**把父节点和子节点连起来；深度往右成一列，子节点上下铺开、父节点垂直居中；
//  支持滚动/缩放，底部显示 Rendered Nodes: X / Y（只画可见的那部分）。
//
//  数据来源（两种模式共用一个内部节点表）：
//    · 预览：CraftTree.Build(...) ⇒ CraftTreeNode（有 Children/Depth/RequiredTotal/Crafts/InCore/CoveredByCore/NoRecipe）
//    · 实时：CraftJob.steps ⇒（有 depth/parent/needCount/crafts + CraftTree.FindPlan 拿进度）
//
//  布局：经典"整齐树"——后序遍历，叶子依次占一行，父节点取其子节点的中点（AE2 同款观感）。
//  ⚠️ 不用 BeginScrollView；整段绘制包 try/catch；不用涉及 Unity 输入之外的 API。
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
    public class Window_AE2CraftTreeGraph : Window
    {
        /// <summary>画一个节点需要的全部信息（预览/实时两种模式都归一到它）。</summary>
        private class GNode
        {
            public ThingDef Def;
            public string Label;
            public string CountText;
            public string StateText;
            public Color StateCol = Color.white;
            public float Progress;
            public int Missing;
            public bool Crafting;   // ★ 正在做 ⇒ 框和连线都亮绿   // ★ AE2：>0 ⇒ 格子外框用缺失红 #ED1C24
            public int DotState;    // ★ 发光状态点：0等待 1进行 2完成 3缺料 4挂起 5阻塞
            public int Depth;
            public int Parent = -1;
            public float X;          // 列（=Depth * 列宽）
            public float Y;          // 行（布局算出来的像素 y）
        }

        private readonly CompBillAutomation comp;
        private readonly CraftJob job;              // 实时模式
        private readonly RecipeDef rootRecipe;      // 预览模式
        private readonly ThingDef rootProduct;
        private int wanted;

        private readonly List<GNode> nodes = new List<GNode>();
        private int builtForWanted = -1;
        private bool builtLive;
        private float scrollX, scrollY;
        private float zoom = 1f;
        private int selected = -1;
        private bool onlyMissing;   // ★ 传统版的「只看缺失」过滤

        // ★ AE2 合成树规格配色（逆向自 ae2ct 1.1.1：全代码绘制，无贴图）
        private static readonly Color AcBlack     = new Color32(0x41, 0x3F, 0x54, 0xFF);   // ★ 描边（AE2 OutlineCol）
        private static readonly Color AcFrame     = new Color32(0x41, 0x3F, 0x54, 0xFF);   // 格子外框
        private static readonly Color AcFill      = new Color32(0x9A, 0x9F, 0xB4, 0xFF);   // 格子填充
        private static readonly Color AcEdge      = new Color32(0xAD, 0xB0, 0xC4, 0xFF);   // 格子亮边
        private static readonly Color AcMissing   = new Color32(0x8B, 0x24, 0x1E, 0xFF);   // ★ 缺失红（AE2 Bad 调深，与灰底协调）
        private static readonly Color AcHighlight = new Color32(0xAC, 0xE9, 0xFF, 0xFF);   // ★ 高亮青（palette.json SELECTION_COLOR，本就是 AE2 色）
        private static readonly Color AcViewport  = new Color32(0x9A, 0x9F, 0xB4, 0xFF);   // ★ 视口底（AE2 内容区色）
        private const float NodeSize = 44f;   // ★ 再放大一倍（贴图 20→44）
        private const float ColGap = 96f;   // ★ 行距（每深一级往下 96）
        private const float RowGap = 34f;   // ★ 兄弟/子树横向间隔（节距 = 44+34 = 78）

        internal Window_AE2CraftTreeGraph(CompBillAutomation comp, CraftJob job)
        {
            this.comp = comp;
            this.job = job;
            this.rootRecipe = (job != null) ? job.rootRecipe : null;
            this.rootProduct = (job != null) ? job.rootProduct : null;
            this.wanted = (job != null) ? Mathf.Max(1, job.wanted) : 1;
            Init();
        }

        public Window_AE2CraftTreeGraph(CompBillAutomation comp, RecipeDef recipe, ThingDef product, int wanted)
        {
            this.comp = comp;
            this.job = null;
            this.rootRecipe = recipe;
            this.rootProduct = product;
            this.wanted = Mathf.Max(1, wanted);
            Init();
        }

        private void Init()
        {
            this.doCloseX = false;
            this.closeOnClickedOutside = false;
            this.absorbInputAroundWindow = false;   // 不挡原版全局按键（空格/加速）
            this.onlyOneOfTypeAllowed = true;
            this.draggable = true;
            this.resizeable = true;
            this.doWindowBackground = false;   // ★ 只用 AE2 边框（true 会在外面再套一圈原版黑底）
        }

        // ===== 节点框全部自绘（见下方逐像素画法）=====
        // ⚠️ 这里**不能**再引用 AE2CT/guicraftingtree_light 这类图集贴图：提交里没有随附该素材，
        //    上游合进去会是一条坏贴图路径（ContentFinder 找不到 ⇒ 节点框整块不画）。
        //    原来用图集精灵做的"缺失红叉 / 选中框"现在都改成自绘（下方 StateText 的 ✕ 与选中描边）。
        // ---- 细线三层：深 / 浅 / 深（自包含，供连线与接头使用）----
        private static void PipeSeg2H(Rect seg, float pw, Color edge, Color core)
        {
            if (seg.width <= 0f) return;
            float half = Mathf.Max(1f, pw * 0.5f);
            Widgets.DrawBoxSolid(new Rect(seg.x, seg.y - half, seg.width, half), edge);
            Widgets.DrawBoxSolid(new Rect(seg.x, seg.y, seg.width, Mathf.Max(1f, pw)), core);
            Widgets.DrawBoxSolid(new Rect(seg.x, seg.y + Mathf.Max(1f, pw), seg.width, half), edge);
        }

        private static void PipeSeg2V(Rect seg, float pw, Color edge, Color core)
        {
            if (seg.height <= 0f) return;
            float half = Mathf.Max(1f, pw * 0.5f);
            Widgets.DrawBoxSolid(new Rect(seg.x - half, seg.y, half, seg.height), edge);
            Widgets.DrawBoxSolid(new Rect(seg.x, seg.y, Mathf.Max(1f, pw), seg.height), core);
            Widgets.DrawBoxSolid(new Rect(seg.x + Mathf.Max(1f, pw), seg.y, half, seg.height), edge);
        }

        public override Vector2 InitialSize { get { return new Vector2(760f, 520f); } }

        public override void DoWindowContents(Rect inRect)
        {
            AE2Draw.HandlePauseHotkey();
            try
            {
                if (comp == null || comp.parent == null || rootRecipe == null) { Close(); return; }
                if (inRect.width < 380f || inRect.height < 220f) { Close(); return; }
                if (job != null)
                {
                    bool closeC;
                    Rect ri = AE2Draw.WindowFrame(inRect.ContractedBy(2f), "DS_AE2_Tree_Live".Translate(rootRecipe.LabelCap, job.wanted).ToString(), out closeC);
                    if (closeC) { Close(); return; }
                    BuildLive();   // ★ 每帧重建状态：进度/颜色/缺料才能实时反映（原来只建一次 ⇒ 树没反应）
                    DrawGraph(ri, true);
                }
                else
                {
                    bool closeC;
                    Rect ri = AE2Draw.WindowFrame(inRect.ContractedBy(2f), "DS_AE2_Tree_Title".Translate(rootRecipe.LabelCap).ToString(), out closeC);
                    if (closeC) { Close(); return; }
                    if (nodes.Count == 0 || builtForWanted != wanted) { BuildPreview(this.comp.parent.Map); builtForWanted = wanted; }
                    DrawGraph(ri, false);
                }
            }
            catch (Exception __uiEx) { Log.ErrorOnce("[DigitalStorage] 合成树（节点）绘制异常（只记一次）：" + __uiEx, 771007); }
        }

        // ================= 数据 =================
        private void BuildPreview(Map map)
        {
            nodes.Clear();
            CraftTreeNode root = CraftTree.Build(comp, rootRecipe, rootProduct, wanted);
            if (root == null) return;
            Dictionary<ThingDef, int> stock = CraftTree.CoreStock(map);
            AddPreviewNode(root, -1, stock);
            TidyLayout();
        }

        private int AddPreviewNode(CraftTreeNode n, int parent, Dictionary<ThingDef, int> stock)
        {
            GNode g = new GNode();
            g.Def = n.Product;
            g.Label = (n.Product != null) ? n.Product.LabelCap.ToString() : "?";
            g.CountText = n.RequiredTotal + " ×" + n.Crafts;
            int have; stock.TryGetValue(n.Product, out have);
            if (n.CoveredByCore) { g.StateText = "DS_AE2_Tree_StInCore".Translate(have).ToString(); g.StateCol = AE2Draw.Accent; g.Progress = 1f; g.DotState = 2; }
            else if (n.NoRecipe) { g.StateText = null; g.StateCol = AE2Draw.Bad; g.DotState = 3; }
            else if (n.CycleCut) { g.StateText = null; g.StateCol = AE2Draw.Bad; g.DotState = 5; }
            else { g.StateText = null; g.StateCol = AE2Draw.Warn; g.DotState = 3; g.Missing = n.Required; }   // ★ 缺失 ⇒ 红框
            g.Depth = n.Depth;
            g.Parent = parent;
            int me = nodes.Count;
            nodes.Add(g);
            for (int i = 0; i < n.Children.Count; i++) AddPreviewNode(n.Children[i], me, stock);
            return me;
        }

        private void BuildLive()
        {
            nodes.Clear();
            if (job == null) return;
            for (int i = 0; i < job.steps.Count; i++)
            {
                CraftJobStep s = job.steps[i];
                if (s == null || s.recipe == null) continue;
                GNode g = new GNode();
                g.Def = s.product;
                g.Label = s.recipe.LabelCap.ToString();
                g.CountText = s.needCount + " ×" + s.crafts;
                g.Depth = Mathf.Max(0, s.depth);
                g.Parent = (s.parent >= 0 && s.parent < i) ? s.parent : -1;
                CraftPlan p = CraftTree.FindPlan(comp, s.recipe);
                CraftLine line = (p != null && p.lines.Count > 0) ? p.lines[0] : null;
                if (p == null) { g.StateText = null; g.StateCol = AE2Draw.Bad; g.DotState = 5; }
                else if (CompBillAutomation.StepSatisfied(p)) { g.StateText = null; g.StateCol = AE2Draw.Accent; g.Progress = 1f; g.DotState = 2; }
                else if (p.suspended) { g.StateText = null; g.StateCol = AE2Draw.TextDimCol; g.DotState = 4; }
                else if (line != null && line.HasWork) { g.Crafting = true; g.Progress = line.Progress01; g.StateText = null; g.StateCol = AE2Draw.Accent; g.DotState = 1; }
                else if (line != null && !string.IsNullOrEmpty(line.BlockKey)) { g.StateText = null; g.StateCol = AE2Draw.Bad; g.DotState = 5; g.Missing = 1; }   // ★ 被挡 ⇒ 红框
                else { g.StateText = null; g.StateCol = AE2Draw.Warn; g.DotState = 0; }
                nodes.Add(g);
            }
            TidyLayout();
        }


        /// <summary>
        /// ★ 整齐树布局（修"两个节点贴一起"）：叶子按顺序占位，**子树返回下一个空位** ⇒ 兄弟子树永不重叠；
        ///   父节点落在其子节点区间中点；纵向 = 深度 × 行距（从上往下一级一级）。
        /// </summary>
        private void TidyLayout()
        {
            if (nodes.Count == 0) return;
            float next = 0f;
            LayoutSubtree(0, ref next);
        }

        private void LayoutSubtree(int idx, ref float next)
        {
            GNode n = nodes[idx];
            n.Y = n.Depth * ColGap;
            int firstChild = -1;
            float l = 0f, rr = 0f; bool any = false;
            for (int i = idx + 1; i < nodes.Count; i++)
            {
                if (nodes[i].Parent != idx) continue;
                if (firstChild < 0) firstChild = i;
                LayoutSubtree(i, ref next);
                float cx = nodes[i].X;
                if (!any) { l = cx; any = true; }
                rr = cx;
            }
            if (firstChild < 0)
            {
                n.X = next;
                next += NodeSize + RowGap;
            }
            else
            {
                n.X = (l + rr) * 0.5f;
                if (next < n.X + NodeSize + RowGap) next = n.X + NodeSize + RowGap;
            }
        }
        private void DrawGraph(Rect ri, bool live)
        {
            // 顶部工具条
            float y = ri.y;
            if (AE2Draw.AE2TextButton(new Rect(ri.x, y, 30f, 22f), "−")) zoom = Mathf.Clamp(zoom - 0.05f, 0.25f, 3f);   // ★ 传统版：步长 0.05，范围 [0.25,1]
            if (AE2Draw.AE2TextButton(new Rect(ri.x + 34f, y, 30f, 22f), "+")) zoom = Mathf.Clamp(zoom + 0.05f, 0.25f, 3f);
            AE2Draw.TextIn(new Rect(ri.x + 70f, y, 200f, 22f), "DS_AE2_Tree_Zoom".Translate((int)(zoom * 100f)).ToString(), 1f, AE2Draw.TextCol, TextAnchor.MiddleLeft);
            if (!live)
            {
                if (AE2Draw.AE2TextButton(new Rect(ri.x + 180f, y, 30f, 22f), "−1")) { wanted = Mathf.Max(1, wanted - 1); nodes.Clear(); }
                if (AE2Draw.AE2TextButton(new Rect(ri.x + 214f, y, 30f, 22f), "+1")) { wanted = Mathf.Min(9999, wanted + 1); nodes.Clear(); }
                AE2Draw.TextIn(new Rect(ri.x + 250f, y, 120f, 22f), "DS_AE2_Tree_Count".Translate(wanted).ToString(), 1f, AE2Draw.TextCol, TextAnchor.MiddleLeft);
                if (AE2Draw.AE2TextButton(new Rect(ri.xMax - 132f, y, 132f, 22f), "DS_AE2_Tree_Submit".Translate().ToString()))   // ★ 主操作：常态按钮
                {
                    CraftJob j = comp.SubmitJob(rootRecipe, rootProduct, wanted, true);
                    if (j != null) Close();
                }
            }
            else if (AE2Draw.AE2TextButton(new Rect(ri.xMax - 132f, y, 132f, 22f), "DS_AE2_Tree_CancelJob".Translate().ToString()))
            {
                comp.CancelJob(job);
                Close();
            }
            y += 26f;

            // ★ 画布改成"平栏 + 头带"（和主面板三栏一致，界面有分界）
            Rect view = new Rect(ri.x, y, ri.width, ri.yMax - y);
            AE2Draw.FlatColumn(view);
            // ★ 头带 24px（FlatColumn 已改），文字容器与内容起点同步放大
            AE2Draw.TextIn(new Rect(view.x + 5f, view.y, view.width - 10f, 24f),
                "DS_AE2_Tree_Btn".Translate().ToString(), 0f, AE2Draw.TextCol, TextAnchor.MiddleLeft);
            view = new Rect(view.x + 5f, view.y + 27f, view.width - 10f, view.height - 32f);

            // 画布尺寸
            float maxX = 0f, maxY = 0f;
            for (int i = 0; i < nodes.Count; i++)
            {
                if (nodes[i].X + NodeSize > maxX) maxX = nodes[i].X + NodeSize;
                if (nodes[i].Y + NodeSize > maxY) maxY = nodes[i].Y + NodeSize;
            }
            maxX *= zoom; maxY *= zoom;
            scrollX = Mathf.Clamp(scrollX, 0f, Mathf.Max(0f, maxX - view.width + 20f));
            scrollY = Mathf.Clamp(scrollY, 0f, Mathf.Max(0f, maxY - view.height + 20f));

            // 滚轮：上下；Shift+滚轮：左右
            // ★ AE2 规格：滚轮 = 缩放（每格 ±0.1，钳制 [0.1,10]），平移靠拖动/后续再加
            if (Mouse.IsOver(view) && Event.current != null && Event.current.type == EventType.ScrollWheel)
            {
                zoom = Mathf.Clamp(zoom - Event.current.delta.y * 0.05f, 0.25f, 3f);   // ★ 传统版滚轮步长
                Event.current.Use();
            }

            // 连线先画（在节点下面）
            for (int i = 0; i < nodes.Count; i++)
            {
                GNode p = nodes[i];
                // ===== 连线：按**子节点**分段着色（谁在做谁的支路才亮）+ 实心接头 =====
                int lc = -1, rc = -1, cc = 0;
                for (int k = 0; k < nodes.Count; k++)
                {
                    if (nodes[k].Parent != i) continue;
                    if (lc < 0 || nodes[k].X < nodes[lc].X) lc = k;
                    if (rc < 0 || nodes[k].X > nodes[rc].X) rc = k;
                    cc++;
                }
                if (cc == 0) continue;

                float pw = Mathf.Max(1f, 2f * zoom);
                float half = Mathf.Max(1f, pw * 0.5f);
                float pCx = Mathf.Round(view.x + 10f - scrollX + (p.X + NodeSize * 0.5f) * zoom);
                float pBot = Mathf.Round(view.y + 10f - scrollY + (p.Y + NodeSize) * zoom);
                float busY = Mathf.Round(view.y + 10f - scrollY + (p.Y + NodeSize + (ColGap - NodeSize) * 0.5f) * zoom);
                Color edgeN = new Color32(0x69, 0x6D, 0x88, 0xFF);   // ★ AE2 深档
                Color coreN = new Color32(0xAD, 0xB0, 0xC4, 0xFF);   // ★ AE2 槽位色

                // 父竖桩（中性色：它通向总线，不代表某个子节点）
                // ★ 修"上半截不亮"：父竖桩与接头按**子树整体状态**上色（有孩子在做事 ⇒ 整条路径一起亮）
                bool upWork = false, upMiss = false;
                for (int k2 = 0; k2 < nodes.Count; k2++)
                {
                    if (nodes[k2].Parent != i) continue;
                    if (nodes[k2].Crafting) upWork = true;
                    if (nodes[k2].Missing > 0) upMiss = true;
                }
                Color upCore = upMiss ? (Color)new Color32(0x8B, 0x24, 0x1E, 0xFF) : (upWork ? (Color)new Color32(0x9C, 0xD3, 0xFF, 0xFF) : coreN);   // ★ 缺=AE2红 / 工=AE2青
                Color upEdge = upMiss ? (Color)new Color32(0x8B, 0x24, 0x1E, 0xFF) : (upWork ? (Color)new Color32(0x16, 0x3B, 0x4E, 0xFF) : edgeN);
                PipeSeg2V(new Rect(pCx, pBot, pw, Mathf.Max(1f, busY - pBot + half)), pw, upEdge, upCore);
                // 接头（实心方块，保证吻合）
                Widgets.DrawBoxSolid(new Rect(pCx - half, busY - half, pw, pw), upCore);

                for (int k = 0; k < nodes.Count; k++)
                {
                    if (nodes[k].Parent != i) continue;
                    GNode c = nodes[k];
                    float ccx = Mathf.Round(view.x + 10f - scrollX + (c.X + NodeSize * 0.5f) * zoom);
                    float cTop = Mathf.Round(view.y + 10f - scrollY + c.Y * zoom);
                    Color core = (c.Missing > 0) ? (Color)new Color32(0x8B, 0x24, 0x1E, 0xFF)
                               : (c.Crafting ? (Color)new Color32(0x9C, 0xD3, 0xFF, 0xFF) : coreN);
                    Color edge = (c.Missing > 0) ? (Color)new Color32(0x8B, 0x24, 0x1E, 0xFF)
                               : (c.Crafting ? (Color)new Color32(0x16, 0x3B, 0x4E, 0xFF) : edgeN);

                    // 该子节点的横向段（父竖桩 → 子节点中心），只给这一截上色
                    float x0 = Mathf.Min(pCx, ccx), x1 = Mathf.Max(pCx, ccx);
                    PipeSeg2H(new Rect(x0, busY, Mathf.Max(pw, x1 - x0), pw), pw, edge, core);
                    // 该子节点的竖桩（总线 → 子节点上缘）
                    PipeSeg2V(new Rect(ccx, busY, pw, Mathf.Max(pw, cTop - busY + half)), pw, edge, core);
                    // 两个接头方块（总线∩横段、总线∩竖桩）
                    Widgets.DrawBoxSolid(new Rect(ccx - half, busY - half, pw, pw), core);
                }            }   // 连线循环收尾（放在所有连线语句之后）
            for (int i = 0; i < nodes.Count; i++)
            {
                GNode n = nodes[i];
                if (n == null) continue;
                float sx = view.x + n.X * zoom + 10f - scrollX;   // ★ 必须加视口原点
                float sy = view.y + n.Y * zoom + 10f - scrollY;
                float sz = NodeSize * zoom;
                Rect r = new Rect(sx, sy, sz, sz);
                if (r.xMax < view.x || r.x > view.xMax || r.yMax < view.y || r.y > view.yMax) continue;   // ★ 修：原来是 r.y < view.yMax（写反了 ⇒ 节点全被剔除）

                // ================= 节点框：★ 照模板造型 =================
                //   模板节点 = AE2 方按钮（44×44）+ 居中的物品图标 + 底部进度条 + 状态标签
                //   （原来是"三层同心矩形"，那是自己弄的，不是 AE2 的东西）
                bool isMissing = (n.Missing > 0);
                bool isCrafting = n.Crafting;
                bool isDone = (n.Progress >= 1f);

                AE2Draw.AE2Button(r, isCrafting || isDone, false);   // 合成中/已完成 ⇒ 选中态浅面

                // 状态色描边（缺料=红 / 合成中=青）—— 叠在 AE2 按钮外圈之上，保留语义色
                if (isMissing) AE2Draw.OutlineColor(r, AE2Draw.Bad);
                else if (isCrafting) AE2Draw.OutlineColor(r, AE2Draw.Accent);

                // 物品图标（居中，占节点内 60%）
                if (n.Def != null)
                {
                    float ip = sz * 0.20f;
                    Widgets.DefIcon(new Rect(r.x + ip, r.y + ip * 0.8f, sz - ip * 2f, sz - ip * 2f), n.Def);
                }

                // 底部进度条（节点内，AE2 深槽 + 青填充）
                float barH = Mathf.Max(4f, 6f * zoom);
                float barY = r.yMax - barH - Mathf.Max(2f, 3f * zoom);
                Rect barR = new Rect(r.x + Mathf.Max(3f, 5f * zoom), barY, sz - 2f * Mathf.Max(3f, 5f * zoom), barH);
                AE2Draw.Bar(barR, n.Progress, false);

                // 状态标签：节点下方（模板是框内小字，环世界字号大 ⇒ 放框下方更清楚）
                // ★★ 状态改用**左上角发光小点**（用户要求：去掉文字标签，要"高级"的彩色光点）
                //     0等待(琥珀) 1进行(青·脉冲) 2完成(亮青) 3缺料(琥珀) 4挂起(灰) 5阻塞(深红)
                float dotS = Mathf.Clamp(11f * zoom, 8f, 20f);
                AE2Draw.GlowDot(new Rect(r.x + 2f, r.y + 2f, dotS, dotS), n.DotState, true);

                // 合成中：节点外圈脉冲光晕（AE2 青）
                if (isCrafting)
                {
                    float pulse = 0.45f + 0.55f * Mathf.Abs(Mathf.Sin(Time.realtimeSinceStartup * 3.2f));
                    byte ga = (byte)(110f * pulse);
                    Color gc = new Color32(0x9C, 0xD3, 0xFF, ga);
                    float o = Mathf.Max(2f, 3f * zoom);
                    Widgets.DrawBoxSolid(new Rect(r.x - o, r.y - o, r.width + 2f * o, 2f), gc);
                    Widgets.DrawBoxSolid(new Rect(r.x - o, r.yMax + o - 2f, r.width + 2f * o, 2f), gc);
                    Widgets.DrawBoxSolid(new Rect(r.x - o, r.y - o, 2f, r.height + 2f * o), gc);
                    Widgets.DrawBoxSolid(new Rect(r.xMax + o - 2f, r.y - o, 2f, r.height + 2f * o), gc);
                }
                // ★ 选中框：AE2 青描边，外扩 2px（原来是绿描边，且引用了已删的 nb 变量）
                if (i == selected)
                    AE2Draw.OutlineColor(new Rect(r.x - 2f, r.y - 2f, r.width + 4f, r.height + 4f), AE2Draw.Accent);
                TooltipHandler.TipRegion(r, (string)(n.Label + "\n" + n.CountText + "  " + n.StateText));
                if (Widgets.ButtonInvisible(r))   // ★ 传统版交互：点节点 ⇒ 选中并聚焦
                {
                    selected = i;
                    scrollX = Mathf.Max(0f, n.X * zoom - view.width * 0.25f);
                    scrollY = Mathf.Max(0f, n.Y * zoom - view.height * 0.25f);
                }
            }

            // 底部：AE2 同款调试行（按用户要求，左下角提示已关闭——想看得自己加回来）
        }
    }

}
