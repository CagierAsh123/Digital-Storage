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
        private static readonly Color AcBlack     = new Color32(0x00, 0x00, 0x00, 0xFF);   // 节点黑块 + 连线
        private static readonly Color AcFrame     = new Color32(0x41, 0x3F, 0x54, 0xFF);   // 格子外框
        private static readonly Color AcFill      = new Color32(0x9A, 0x9F, 0xB4, 0xFF);   // 格子填充
        private static readonly Color AcEdge      = new Color32(0xAD, 0xB0, 0xC4, 0xFF);   // 格子亮边
        private static readonly Color AcMissing   = new Color32(0xED, 0x1C, 0x24, 0xFF);   // 缺失红
        private static readonly Color AcHighlight = new Color32(0xAC, 0xE9, 0xFF, 0xFF);   // 高亮青
        private static readonly Color AcViewport  = new Color32(0x72, 0x72, 0x72, 0x66);   // 视口底 #727272@40%
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

        // ===== 原模组素材（AE2CT-Legacy 控制图集 256x256；UV 表来自字节码常量 + 像素双验证）=====
        private static Texture2D Atlas;
        private const float AtlasSize = 256f;

        /// <summary>按图集 UV 矩形贴一块精灵。注意 Unity 纹理 v 轴自下而上 ⇒ v 要翻转。</summary>
        private static void Blit(Rect dest, float u, float v, float w, float h)
        {
            if (Atlas == null)
            {
                Atlas = ContentFinder<Texture2D>.Get("AE2CT/guicraftingtree_light", false);
                if (Atlas != null) Atlas.filterMode = FilterMode.Point;   // ★ 放大时保持像素锐利
                if (Atlas == null) return;
            }
            Rect uv = new Rect(u / AtlasSize, 1f - (v + h) / AtlasSize, w / AtlasSize, h / AtlasSize);
            GUI.DrawTextureWithTexCoords(dest, Atlas, uv);
        }
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
            if (n.CoveredByCore) { g.StateText = "核心 " + have; g.StateCol = AE2Draw.Accent; g.Progress = 1f; }
            else if (n.NoRecipe) { g.StateText = "无配方 " + have; g.StateCol = AE2Draw.Bad; }
            else if (n.CycleCut) { g.StateText = "循环"; g.StateCol = AE2Draw.Bad; }
            else { g.StateText = "缺 " + n.Required; g.StateCol = AE2Draw.Warn; g.Missing = n.Required; }   // ★ 缺失 ⇒ 红框
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
                if (p == null) { g.StateText = "无订单"; g.StateCol = AE2Draw.Bad; }
                else if (CompBillAutomation.StepSatisfied(p)) { g.StateText = "完成"; g.StateCol = AE2Draw.Accent; g.Progress = 1f; }
                else if (p.suspended) { g.StateText = "挂起"; g.StateCol = AE2Draw.TextDimCol; }
                else if (line != null && line.HasWork) { g.Crafting = true; g.Progress = line.Progress01; g.StateText = (int)(g.Progress * 100f) + "%"; g.StateCol = AE2Draw.Accent; }
                else if (line != null && !string.IsNullOrEmpty(line.BlockKey)) { g.StateText = line.BlockKey.Translate().ToString(); g.StateCol = AE2Draw.Bad; g.Missing = 1; }   // ★ 被挡 ⇒ 红框
                else { g.StateText = "等台子"; g.StateCol = AE2Draw.Warn; }
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
            if (AE2Draw.TextButton(new Rect(ri.x, y, 30f, 22f), "−")) zoom = Mathf.Clamp(zoom - 0.05f, 0.25f, 3f);   // ★ 传统版：步长 0.05，范围 [0.25,1]
            if (AE2Draw.TextButton(new Rect(ri.x + 34f, y, 30f, 22f), "+")) zoom = Mathf.Clamp(zoom + 0.05f, 0.25f, 3f);
            AE2Draw.Tiny(new Rect(ri.x + 70f, y + 3f, 200f, 17f), "缩放 " + (int)(zoom * 100f) + "%", AE2Draw.TextCol);
            if (!live)
            {
                if (AE2Draw.TextButton(new Rect(ri.x + 180f, y, 30f, 22f), "−1")) { wanted = Mathf.Max(1, wanted - 1); nodes.Clear(); }
                if (AE2Draw.TextButton(new Rect(ri.x + 214f, y, 30f, 22f), "+1")) { wanted = Mathf.Min(9999, wanted + 1); nodes.Clear(); }
                AE2Draw.Tiny(new Rect(ri.x + 250f, y + 3f, 120f, 17f), "DS_AE2_Tree_Count".Translate(wanted).ToString(), AE2Draw.TextCol);
                if (AE2Draw.TextButton(new Rect(ri.xMax - 132f, y, 132f, 22f), "DS_AE2_Tree_Submit".Translate().ToString(), true))
                {
                    CraftJob j = comp.SubmitJob(rootRecipe, rootProduct, wanted, true);
                    if (j != null) Close();
                }
            }
            else if (AE2Draw.TextButton(new Rect(ri.xMax - 132f, y, 132f, 22f), "DS_AE2_Tree_CancelJob".Translate().ToString()))
            {
                comp.CancelJob(job);
                Close();
            }
            y += 26f;

            Rect view = new Rect(ri.x, y, ri.width, ri.yMax - y);
            AE2Draw.Sunken(view, AE2Draw.Slot);   // ★ 统一成面板风格：下沉凹槽

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

            int rendered = 0;
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
                Color edgeN = new Color32(0x6E, 0x72, 0x86, 0xFF);
                Color coreN = new Color32(0xD8, 0xDC, 0xE8, 0xFF);

                // 父竖桩（中性色：它通向总线，不代表某个子节点）
                // ★ 修"上半截不亮"：父竖桩与接头按**子树整体状态**上色（有孩子在做事 ⇒ 整条路径一起亮）
                bool upWork = false, upMiss = false;
                for (int k2 = 0; k2 < nodes.Count; k2++)
                {
                    if (nodes[k2].Parent != i) continue;
                    if (nodes[k2].Crafting) upWork = true;
                    if (nodes[k2].Missing > 0) upMiss = true;
                }
                Color upCore = upMiss ? (Color)new Color32(0xE8, 0x6B, 0x6B, 0xFF) : (upWork ? (Color)new Color32(0x64, 0xFF, 0x8F, 0xFF) : coreN);
                Color upEdge = upMiss ? (Color)new Color32(0x5A, 0x2A, 0x2A, 0xFF) : (upWork ? (Color)new Color32(0x1E, 0x5C, 0x32, 0xFF) : edgeN);
                PipeSeg2V(new Rect(pCx, pBot, pw, Mathf.Max(1f, busY - pBot + half)), pw, upEdge, upCore);
                // 接头（实心方块，保证吻合）
                Widgets.DrawBoxSolid(new Rect(pCx - half, busY - half, pw, pw), upCore);

                for (int k = 0; k < nodes.Count; k++)
                {
                    if (nodes[k].Parent != i) continue;
                    GNode c = nodes[k];
                    float ccx = Mathf.Round(view.x + 10f - scrollX + (c.X + NodeSize * 0.5f) * zoom);
                    float cTop = Mathf.Round(view.y + 10f - scrollY + c.Y * zoom);
                    Color core = (c.Missing > 0) ? (Color)new Color32(0xE8, 0x6B, 0x6B, 0xFF)
                               : (c.Crafting ? (Color)new Color32(0x64, 0xFF, 0x8F, 0xFF) : coreN);
                    Color edge = (c.Missing > 0) ? (Color)new Color32(0x5A, 0x2A, 0x2A, 0xFF)
                               : (c.Crafting ? (Color)new Color32(0x1E, 0x5C, 0x32, 0xFF) : edgeN);

                    // 该子节点的横向段（父竖桩 → 子节点中心），只给这一截上色
                    float x0 = Mathf.Min(pCx, ccx), x1 = Mathf.Max(pCx, ccx);
                    PipeSeg2H(new Rect(x0, busY, Mathf.Max(pw, x1 - x0), pw), pw, edge, core);
                    // 该子节点的竖桩（总线 → 子节点上缘）
                    PipeSeg2V(new Rect(ccx, busY, pw, Mathf.Max(pw, cTop - busY + half)), pw, edge, core);
                    // 两个接头方块（总线∩横段、总线∩竖桩）
                    Widgets.DrawBoxSolid(new Rect(ccx - half, busY - half, pw, pw), core);
                }            }   // 连线循环收尾（放在所有连线语句之后）
            int vis = 0;   // ★ 节点计数（从粘连行里拆出来）
            for (int i = 0; i < nodes.Count; i++)
            {
                GNode n = nodes[i];
                float sx = view.x + n.X * zoom + 10f - scrollX;   // ★ 必须加视口原点
                float sy = view.y + n.Y * zoom + 10f - scrollY;
                float sz = NodeSize * zoom;
                Rect r = new Rect(sx, sy, sz, sz);
                if (r.xMax < view.x || r.x > view.xMax || r.yMax < view.y || r.y > view.yMax) continue;   // ★ 修：原来是 r.y < view.yMax（写反了 ⇒ 节点全被剔除）
                vis++;
                // ★ AE2 规格：38x38 黑块(+8,+8) → 22x22 格子底板 → 图标 → 数量小字
                // （面板风格不画 AE2CT 的 38x38 黑块）
                // ★ 统一成面板风格：AE2 凹槽格子 + 缺失时红描边
                bool __missing = (n != null && n.Missing > 0);
                // （格子改为逐像素画：见下方图集级画法）
                Rect nodeSpr = new Rect(r.x, r.y + 2f, r.width, r.height);
                if (n != null && n.Missing > 0) Blit(nodeSpr, 40f, 216f, 20f, 20f);   // ★ 原模组：缺失红叉精灵
                if (false)   // 旧的绿框画法已由精灵取代
                {
                    Color selC = new Color32(0x64, 0xFF, 0x8F, 0xFF);
                    Widgets.DrawBoxSolid(new Rect(r.x - 1f, r.y - 1f, r.width + 2f, 1f), selC);
                    Widgets.DrawBoxSolid(new Rect(r.x - 1f, r.yMax, r.width + 2f, 1f), selC);
                    Widgets.DrawBoxSolid(new Rect(r.x - 1f, r.y, 1f, r.height), selC);
                    Widgets.DrawBoxSolid(new Rect(r.xMax, r.y, 1f, r.height), selC);
                }
                // ================= 自绘节点框（不用原素材，自己搓）=================
                // 三态各一套 4 层倒角：外描边 / 左上高光 / 面 / 右下暗边；放大不糊、颜色可控
                Color nOut, nHi, nFill, nLo;
                if (n != null && n.Missing > 0)
                {
                    nOut = new Color32(0x5D, 0x36, 0x36, 0xFF); nHi = new Color32(0xF5, 0xEF, 0xEF, 0xFF);
                    nFill = new Color32(0xDC, 0xC3, 0xC3, 0xFF); nLo = new Color32(0xA5, 0x77, 0x77, 0xFF);
                }
                else if (n != null && n.Crafting)
                {
                    nOut = new Color32(0x1E, 0x5C, 0x32, 0xFF); nHi = new Color32(0xE6, 0xFF, 0xEE, 0xFF);
                    nFill = new Color32(0x64, 0xFF, 0x8F, 0xFF); nLo = new Color32(0x2F, 0x8F, 0x4F, 0xFF);
                }
                else
                {
                    nOut = new Color32(0x41, 0x3F, 0x54, 0xFF); nHi = new Color32(0xF2, 0xF2, 0xF2, 0xFF);
                    nFill = new Color32(0xCB, 0xCC, 0xD4, 0xFF); nLo = new Color32(0x87, 0x8F, 0xA5, 0xFF);
                }
                // ★ 边缘深、中间浅（照参考图）：三层同心矩形 —— 外深描边 → 中环 → 亮芯
                float nb = Mathf.Max(1f, 1f * zoom);
                float mid = Mathf.Max(1f, 2.5f * zoom);
                Color nEdge, nMidTone, nCore;
                if (n != null && n.Missing > 0)
                {
                    nEdge = new Color32(0x4A, 0x24, 0x24, 0xFF); nMidTone = new Color32(0xB0, 0x7A, 0x7A, 0xFF); nCore = new Color32(0xF6, 0xE4, 0xE4, 0xFF);
                }
                else if (n != null && n.Crafting)
                {
                    nEdge = new Color32(0x14, 0x4A, 0x26, 0xFF); nMidTone = new Color32(0x3E, 0xB0, 0x62, 0xFF); nCore = new Color32(0x9C, 0xFF, 0xBC, 0xFF);
                }
                else
                {
                    nEdge = new Color32(0x2E, 0x2F, 0x3C, 0xFF); nMidTone = new Color32(0x87, 0x8F, 0xA5, 0xFF); nCore = new Color32(0xEC, 0xED, 0xF3, 0xFF);
                }
                Widgets.DrawBoxSolid(r, nEdge);
                Widgets.DrawBoxSolid(r.ContractedBy(nb), nMidTone);
                Widgets.DrawBoxSolid(r.ContractedBy(nb + mid), nCore);
                if (n != null && n.Crafting)   // ★ 合成中：框外圈脉冲光晕（与连线一起"发光"）
                {
                    float pulse = 0.45f + 0.55f * Mathf.Abs(Mathf.Sin(Time.realtimeSinceStartup * 3.2f));
                    for (int gi2 = 1; gi2 <= 3; gi2++)
                    {
                        byte ga = (byte)(110f * pulse / gi2);
                        Color gc = new Color32(0x64, 0xFF, 0x8F, ga);
                        float o = nb + gi2 * 2f;
                        Widgets.DrawBoxSolid(new Rect(r.x - o, r.y - o, r.width + 2f * o, 2f), gc);
                        Widgets.DrawBoxSolid(new Rect(r.x - o, r.yMax + o - 2f, r.width + 2f * o, 2f), gc);
                        Widgets.DrawBoxSolid(new Rect(r.x - o, r.y - o, 2f, r.height + 2f * o), gc);
                        Widgets.DrawBoxSolid(new Rect(r.xMax + o - 2f, r.y - o, 2f, r.height + 2f * o), gc);
                    }
                }                if (i == selected) Blit(nodeSpr, 0f, 196f, 20f, 20f);                 // ★ 原模组：选中框精灵
                if (n.Def != null) Widgets.DefIcon(new Rect(r.x + 5f * zoom, r.y + 5f * zoom, 34f * zoom, 34f * zoom), n.Def);   // ★ 物品图标（放在框之后 ⇒ 盖在框上）
                // ★ 悬浮标签：节点右下角显示"进度/需要量"（模仿原版 22/100 那种浮字）
                if (n != null && !string.IsNullOrEmpty(n.CountText))
                {
                    string lab = (n.Progress > 0f && n.Progress < 1f) ? ((int)(n.Progress * 100f) + "%") : n.CountText;
                // ★ 已按用户要求删除：Rect tag = new Rect(r.x - 6f, r.yMax + 2f, r.width + 12f, 18f * zoom);   // ★ 标签画在节点下方
                // ★ 已按用户要求删除：Widgets.DrawBoxSolid(tag, new Color32(0x1B, 0x1C, 0x24, 0xE0));
                // ★ 已按用户要求删除：AE2Draw.Tiny(new Rect(tag.x + 2f, tag.y - 1f, tag.width - 4f, tag.height + 2f), lab, AE2Draw.Hi);
                if (n.Progress > 0f && n.Progress < 1f)
                {
                    string pct = (int)(n.Progress * 100f) + "%";
                    Rect tagP = new Rect(r.x + r.width * 0.15f, r.yMax + 2f, r.width * 0.7f, 18f * zoom);
                    Widgets.DrawBoxSolid(tagP, new Color32(0x1B, 0x1C, 0x24, 0xD8));
                    AE2Draw.Tiny(new Rect(tagP.x + 2f, tagP.y - 1f, tagP.width - 4f, tagP.height + 3f), pct, AE2Draw.Hi);
                }
                }
                if (n != null && n.Missing > 0)
                    AE2Draw.Tiny(new Rect(r.x + r.width * 0.2f, r.y - 2f, r.width * 0.6f, r.height + 4f), "✕", new Color32(0xC1, 0x42, 0x4B, 0xFF));
                if (i == selected)
                {
                    Color sel2 = new Color32(0x64, 0xFF, 0x8F, 0xFF);
                    Widgets.DrawBoxSolid(new Rect(r.x - nb - 2f, r.y - nb - 2f, r.width + 2f * nb + 4f, 2f), sel2);
                    Widgets.DrawBoxSolid(new Rect(r.x - nb - 2f, r.yMax + nb, r.width + 2f * nb + 4f, 2f), sel2);
                    Widgets.DrawBoxSolid(new Rect(r.x - nb - 2f, r.y - nb, 2f, r.height + 2f * nb), sel2);
                    Widgets.DrawBoxSolid(new Rect(r.xMax + nb, r.y - nb, 2f, r.height + 2f * nb), sel2);
                }                {
                    TooltipHandler.TipRegion(r, (string)(n.Label + "\n" + n.CountText + "  " + n.StateText));
                    if (Widgets.ButtonInvisible(r))   // ★ 传统版交互：点节点 ⇒ 选中并聚焦
                {
                    selected = i;
                    scrollX = Mathf.Max(0f, n.X * zoom - view.width * 0.25f);
                    scrollY = Mathf.Max(0f, n.Y * zoom - view.height * 0.25f);
                }
                }
            }

            // 底部：AE2 同款调试行
                // ★ 用户要求：左下角提示已关闭 → AE2Draw.Tiny(new Rect(view.x + 2f, view.yMax - 18f, view.width - 4f, 17f),
                // ★ 用户要求：左下角提示已关闭 → "Rendered Nodes: " + vis + " / " + nodes.Count + "   （滚轮上下滚动；点节点看提示）", AE2Draw.TextDimCol);
        }
    }

}
