// =====================================================================================
//  【本地新增文件】AE2Draw —— AE2 风格的共用绘制库
// -------------------------------------------------------------------------------------
//  为什么单独一个文件：制作面板、加配方窗、下单窗、状态窗……都要画同一套东西。
//
//  ★ 第 3 版（用户定案）：**按高版本 AE2 的贴图原样还原**，并支持暗/亮两套主题。
//    · 色值逐像素采样自 AE2 Dark Mode 资源包（见 AE2Theme.cs 的注释），不再自己估。
//    · **保留立体感**（用户明确要）：按 button.png / background.png 的真实行结构画：
//        凸起 = 1px 黑描边 + 上/左高光 + 面 + 下/右阴影 + 底两三行更深阴影
//        凹陷 = 方向相反（上/左暗，下/右亮）
//    · 高光/阴影色取自**当前主题的色阶**（不是纯白/纯黑）—— 这是暗色下也有立体感的关键。
//
//  ⚠️ 所有**方法签名保持不变**（面板那 800+ 行调用本文件），换材质只改这里 + AE2Theme。
//
//  ⚠️ 硬约束（踩过的坑）：
//    1) 自绘窗口**不要** BeginScrollView / BeginClip（不成对 ⇒ GUI 栈失衡 ⇒ 整个界面点不动）；
//       滚动一律用本文件的 DragBar（自绘 + 自己处理鼠标事件）。
//    2) C# 7.3 不接受 Color32 与 Color 混用的三元表达式（CS8957）⇒ 颜色一律用 Color 类型。
// =====================================================================================
using UnityEngine;
using Verse;
using RimWorld;   // ★ KeyBindingDefOf（暂停键绑定）在这个命名空间

namespace DigitalStorage.UI
{
    internal static class AE2Draw
    {
        // =====================================================================
        //  配色 —— 现在从 AE2Theme.Active 读（运行时切主题）。名字保持旧版不变。
        // =====================================================================

        public static Color Panel       { get { return AE2Theme.Active.Panel; } }
        public static Color PanelFace   { get { return AE2Theme.Active.PanelFace; } }
        public static Color Button      { get { return AE2Theme.Active.Button; } }
        public static Color ButtonHover { get { return AE2Theme.Active.ButtonHover; } }
        public static Color Slot        { get { return AE2Theme.Active.Slot; } }
        public static Color SlotHover   { get { return AE2Theme.Active.SlotHover; } }
        public static Color BarBg       { get { return AE2Theme.Active.BarBg; } }

        /// <summary>旧名 Hi（高光）⇒ 当前主题的高光边。</summary>
        public static Color Hi          { get { return AE2Theme.Active.TextHi; } }
        /// <summary>旧名 Lo（阴影）⇒ 当前主题的描边。</summary>
        public static Color Lo          { get { return AE2Theme.Active.OutlineCol; } }
        public static Color OutlineCol  { get { return AE2Theme.Active.OutlineCol; } }
        public static Color LineSoft    { get { return AE2Theme.Active.LineSoft; } }
        // ★ 按钮 hover 态（照搬 button_highlighted.png）：面青 / 顶底高光 / 底阴影 / 上缘白描边
        public static Color BtnHoverFace   { get { return AE2Theme.Active.BtnHoverFace; } }
        public static Color BtnHoverTop    { get { return AE2Theme.Active.BtnHoverTop; } }
        public static Color BtnHoverShadow { get { return AE2Theme.Active.BtnHoverShadow; } }
        public static Color BtnHoverEdge   { get { return AE2Theme.Active.BtnHoverEdge; } }

        public static Color EdgeHi      { get { return AE2Theme.Active.EdgeHi; } }
        public static Color EdgeLo      { get { return AE2Theme.Active.EdgeLo; } }
        public static Color EdgeDeep    { get { return AE2Theme.Active.EdgeDeep; } }

        public static Color TextCol     { get { return AE2Theme.Active.TextCol; } }
        public static Color TextDimCol  { get { return AE2Theme.Active.TextDimCol; } }
        public static Color TextHi      { get { return AE2Theme.Active.TextHi; } }
        /// <summary>落在**控件面**上的正文（浅色主题下控件面比面板深/浅不同，需要专门的字色）。</summary>
        public static Color ButtonText    { get { return AE2Theme.Active.ButtonText; } }
        public static Color ButtonTextDim { get { return AE2Theme.Active.ButtonTextDim; } }

        public static Color Accent        { get { return AE2Theme.Active.Accent; } }
        /// <summary>强调色**当文字**时用的版本（浅色主题下比 Accent 深，保证可读）。</summary>
        public static Color AccentText    { get { return AE2Theme.Active.AccentText; } }
        public static Color AccentBright  { get { return AE2Theme.Active.AccentBright; } }
        public static Color AccentDim     { get { return AE2Theme.Active.AccentDim; } }
        public static Color Warn          { get { return AE2Theme.Active.Warn; } }
        public static Color Bad           { get { return AE2Theme.Active.Bad; } }
        public static Color Ok            { get { return AE2Theme.Active.Ok; } }
        public static Color Bg            { get { return AE2Theme.Active.Bg; } }

        /// <summary>当前主题是不是暗色（界面里想按主题微调间距/文字时可查）。</summary>
        public static bool IsDark { get { return AE2Theme.IsDark; } }

        // =====================================================================
        //  立体边原语（按 AE2 贴图行结构画的）
        // =====================================================================

        /// <summary>
        /// 凸起：1px 描边 + 上/左高光 + 下/右阴影 + 再往上一行更深阴影。
        /// 对应 AE2 button.png 的行结构：
        ///   y0 描边 / y1 高光 / y2..面 / yEnd-3 高光 / yEnd-2..-4 深阴影 / yEnd 描边
        /// </summary>
        /// <summary>
        /// 凸起（控件面）：**严格照搬 AE2 button.png 的 7 段结构**（逐像素实测，nine_slice border=3）。
        ///
        /// <para>贴图纵向结构：</para>
        /// <list type="bullet">
        /// <item>y0        描边      <c>#413F54</c></item>
        /// <item>y1        顶高光    <c>#ADB0C4</c> ← 比面亮</item>
        /// <item>y2..h-6   面        <c>#9A9FB4</c></item>
        /// <item>y(h-5)    底高光    <c>#ADB0C4</c> ← 又亮回来（关键夹层）</item>
        /// <item>y(h-4..-2) 底阴影   <c>#696D88</c> ← 另一个色阶，不是"更暗的面"</item>
        /// <item>y(h-1)    描边</item>
        /// </list>
        /// <para>★ 左右两侧**只有描边，没有高光** —— 旧版四面都画双色边是"不像"的主因。</para>
        /// </summary>
        public static void BevelRaised(Rect r, Color face)
        {
            float x = r.x, y = r.y, w = r.width, h = r.height;

            Widgets.DrawBoxSolid(new Rect(x + 1f, y + 1f, w - 2f, h - 2f), face);
            Widgets.DrawBoxSolid(new Rect(x + 1f, y + 1f, w - 2f, 1f), Slot);          // y1 顶高光
            if (h > 5f)
            {
                Widgets.DrawBoxSolid(new Rect(x + 1f, y + h - 5f, w - 2f, 1f), Slot);  // 底高光（夹层）
                Widgets.DrawBoxSolid(new Rect(x + 1f, y + h - 4f, w - 2f, 3f), EdgeDeep); // 底阴影 3px
            }
            Widgets.DrawBoxSolid(new Rect(x + 1f, y, w - 2f, 1f), OutlineCol);         // 上描边
            Widgets.DrawBoxSolid(new Rect(x + 1f, y + h - 1f, w - 2f, 1f), OutlineCol); // 下描边
            Widgets.DrawBoxSolid(new Rect(x, y + 1f, 1f, h - 2f), OutlineCol);           // 左描边（无高光）
            Widgets.DrawBoxSolid(new Rect(x + w - 1f, y + 1f, 1f, h - 2f), OutlineCol);  // 右描边（无高光）
        }
        /// <summary>
        /// 凹陷：方向与凸起相反（上/左暗，下/右亮）。
        /// 对应 AE2 background.png：顶=近黑 / 面 / 底=中间档。
        /// </summary>
        /// <summary>
        /// 凹陷（深槽面）：顶/左暗、底/右亮，**左右只有描边**。
        /// 对应 AE2 <c>vertical_buttons_bg.png</c>：左 1px <c>#F2F2F2</c> 高光 + 面 + 底高光 1px + 底阴影 2px。
        /// </summary>
        public static void BevelSunken(Rect r, Color face)
        {
            float x = r.x, y = r.y, w = r.width, h = r.height;

            Widgets.DrawBoxSolid(r, face);
            Widgets.DrawBoxSolid(new Rect(x + 1f, y + 1f, w - 2f, 1f), EdgeLo);          // 顶：暗
            if (h > 5f)
            {
                Widgets.DrawBoxSolid(new Rect(x + 1f, y + h - 4f, w - 2f, 1f), EdgeHi);  // 底高光
                Widgets.DrawBoxSolid(new Rect(x + 1f, y + h - 3f, w - 2f, 2f), LineSoft);// 底阴影 2px
            }
            Widgets.DrawBoxSolid(new Rect(x, y, w, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(x, y + h - 1f, w, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(x, y, 1f, h), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(x + w - 1f, y, 1f, h), OutlineCol);
        }
        /// <summary>只有 1px 描边、不带明暗（用于内部细框）。</summary>
        public static void Outline(Rect r)
        {
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, 1f, r.height), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(r.xMax - 1f, r.y, 1f, r.height), OutlineCol);
        }

        /// <summary>用强调色描边（选中态）。</summary>
        public static void OutlineAccentBox(Rect r)
        {
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), Accent);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), Accent);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, 1f, r.height), Accent);
            Widgets.DrawBoxSolid(new Rect(r.xMax - 1f, r.y, 1f, r.height), Accent);
        }

        // =====================================================================
        //  面板 / 槽位 / 凹槽 / 按钮 / 行 / 条
        // =====================================================================

        /// <summary>
        /// 面板：**扁平填充 + 1px 细线**。
        ///
        /// <para>★ 按官方贴图纠正：放大 <c>terminal.png</c> 的槽位区看，AE2 **不是**用凸起/凹陷画槽位的 ——
        /// 而是「浅底 <c>#CBCCD4</c> + 灰紫槽 <c>#ADB0C4</c> + 1px 细网格线 <c>#878FA5</c>」，
        /// 而且**顶边偏暗、底边偏亮**（与凸起方向相反）。
        /// 旧版把槽位也画成双色立体边 ⇒ 观感偏"千禧年桌面 app"，这就是"不像"的主因。</para>
        /// </summary>
        public static void PanelBox(Rect r)
        {
            Widgets.DrawBoxSolid(r, Panel);
            Outline(r);
        }

        /// <summary>需要从底上浮一档的块（标题带 / 详情头）：亮一档的平色 + 细线。</summary>
        public static void PanelRaised(Rect r)
        {
            Widgets.DrawBoxSolid(r, PanelFace);
            Outline(r);
        }

        /// <summary>
        /// 槽位：**平色 + 1px 细线**（顶边稍暗、底边稍亮）。
        /// 这是 AE2 物品格的真实画法（见 terminal.png 放大图），不是内凹立体边。
        /// </summary>
        /// <summary>
        /// 槽位：**外圈 1px 高光 <c>#F2F2F2</c> + 内部槽色 <c>#ADB0C4</c>**。
        /// 这是 AE2 <c>terminal.png</c> 的真实画法（实测槽块 x=8..23 即 16px，缝 2px 露底色）。
        /// 不是内凹立体边，也不是四边描线。
        /// </summary>
        public static void SlotBox(Rect r, bool hover)
        {
            Widgets.DrawBoxSolid(r, hover ? SlotHover : EdgeHi);      // 外圈高光
            if (r.width > 2f && r.height > 2f)
                Widgets.DrawBoxSolid(new Rect(r.x + 1f, r.y + 1f, r.width - 2f, r.height - 2f), Slot);
        }
        /// <summary>凹槽（进度条底 / 信息带底）：深一档的平色 + 细线（同上，不做立体边）。</summary>
        /// <summary>凹槽（进度条底 / 信息带底 / 输入框）：顶部 1px 高光 + 面 + 底高光 + 底阴影。</summary>
        public static void Sunken(Rect r, Color fill)
        {
            float x = r.x, y = r.y, w = r.width, h = r.height;
            Widgets.DrawBoxSolid(r, fill);
            Widgets.DrawBoxSolid(new Rect(x + 1f, y + 1f, w - 2f, 1f), EdgeHi);
            if (h > 4f) Widgets.DrawBoxSolid(new Rect(x + 1f, y + h - 3f, w - 2f, 1f), LineSoft);
            Widgets.DrawBoxSolid(new Rect(x, y, w, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(x, y + h - 1f, w, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(x, y, 1f, h), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(x + w - 1f, y, 1f, h), OutlineCol);
        }
        /// <summary>细分割线（横向）。</summary>
        public static void Divider(Rect r)
        {
            Widgets.DrawBoxSolid(new Rect(r.x, r.y + Mathf.Floor(r.height * 0.5f), r.width, 1f), LineSoft);
        }

        /// <summary>细分割线（纵向）。</summary>
        public static void DividerV(Rect r)
        {
            Widgets.DrawBoxSolid(new Rect(r.x + Mathf.Floor(r.width * 0.5f), r.y, 1f, r.height), LineSoft);
        }

        /// <summary>小节标题：弱色小字 + 下面一条细线。返回去掉标题后的内容区。</summary>
        public static Rect SectionHeader(Rect r, string title)
        {
            const float h = 17f;
            Rect head = new Rect(r.x, r.y, r.width, h);
            Tiny(new Rect(head.x, head.y, head.width, 15f), title, TextDimCol);
            Divider(new Rect(head.x, head.yMax - 2f, head.width, 4f));
            return new Rect(r.x, r.y + h, r.width, r.height - h);
        }

        /// <summary>按钮（AE2 原版结构）：active = 选中态（槽位浅面 + 1px 描边）。</summary>
        public static void FlatButton(Rect r, bool active)
        {
            if (active)
            {
                Widgets.DrawBoxSolid(r, Slot);
                Outline(r);
            }
            else AE2Button(r, false, false);
        }

        /// <summary>按钮悬停版（面亮一档）。</summary>
        public static void FlatButtonHover(Rect r, bool active)
        {
            if (active) { FlatButton(r, true); return; }
            AE2Button(r, false, Mouse.IsOver(r));   // ★ 悬停整块青
        }

        /// <summary>列表行三态（选中 = 强调底 + 左侧亮条 + 青边）。</summary>
        /// <summary>列表行：常态 = 按钮面平色；悬停 = 亮一档；选中 = 槽位色 + 1px 描边（AE2 的选中做法）。</summary>
        public static void Row(Rect r, bool hover, bool selected)
        {
            if (selected)
            {
                Widgets.DrawBoxSolid(r, Slot);
                Outline(r);
                return;
            }
            Widgets.DrawBoxSolid(r, hover ? ButtonHover : Button);
        }
        /// <summary>进度/容量条：凹陷槽 + 填充（满 = 强调，未满 = 琥珀，选中 = 强调亮）。</summary>
        /// <summary>进度条：深槽 <c>#696D88</c> + 1px 描边 + 填充（照 AE2 craft_amt 观感）。</summary>
        public static void Bar(Rect r, float frac, bool selected)
        {
            float x = r.x, y = r.y, w = r.width, h = r.height;
            Widgets.DrawBoxSolid(r, BarBg);
            Widgets.DrawBoxSolid(new Rect(x, y, w, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(x, y + h - 1f, w, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(x, y, 1f, h), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(x + w - 1f, y, 1f, h), OutlineCol);

            float f = Mathf.Clamp01(frac);
            if (f <= 0f) return;
            Color c = selected ? AccentBright : ((f >= 1f) ? Accent : Warn);
            if (w > 2f && h > 2f)
                Widgets.DrawBoxSolid(new Rect(x + 1f, y + 1f, Mathf.Max(1f, (w - 2f) * f), h - 2f), c);
        }
        /// <summary>
        /// 侧面可拖动的滚动条：按住滑块拖 / 点轨道跳 / 滚轮滚，都会改 frac（0~1）。
        /// 滑块长度 = 可见比例 viewPct。**不用 BeginScrollView**（见文件头约束 1）。
        /// </summary>
        // ── 滚动条（★ 照 AE2 官方 small_scroller.png / big_scroller.png 的精确行结构）──
        //   为什么不用 AE2Button：那是给 20px 高按钮设计的 9-slice，在 12×15 的滑块上
        //   border(3)+shadow(3) 会被钳制 ⇒ 结构崩坏成一根黑柱（实测踩过）。
        private static void ScrollTrack(Rect r)
        {
            Widgets.DrawBoxSolid(r, EdgeDeep);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, 1f, r.height), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(r.xMax - 1f, r.y, 1f, r.height), OutlineCol);
        }

        /// <summary>AE2 滚动滑块（12×15 结构：描边 / 顶部高光 / 面 / 底部高光 / 底部阴影 / 描边）。</summary>
        private static void ScrollThumb(Rect r, bool hover)
        {
            float h = r.height;
            if (h < 8f) h = 8f;
            float face = Mathf.Max(1f, h - 6f);
            Color c = hover ? ButtonHover : Button;
            Widgets.DrawBoxSolid(r, c);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y + 1f, r.width, 1f), Slot);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y + 1f + face, r.width, 1f), Slot);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 3f, r.width, 2f), EdgeDeep);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), OutlineCol);
        }
        public static void DragBar(Rect r, ref float frac, float viewPct)
        {
            ScrollTrack(r);
            float thumbH = Mathf.Clamp(r.height * Mathf.Clamp01(viewPct), 22f, r.height);
            float travel = Mathf.Max(1f, r.height - thumbH);

            Event e = Event.current;
            if (e != null && Mouse.IsOver(r))
            {
                if ((e.type == EventType.MouseDown || e.type == EventType.MouseDrag) && e.button == 0)
                {
                    frac = Mathf.Clamp01((e.mousePosition.y - r.y - thumbH * 0.5f) / travel);
                    e.Use();
                }
                else if (e.type == EventType.ScrollWheel)
                {
                    frac = Mathf.Clamp01(frac + e.delta.y * 0.06f);
                    e.Use();
                }
            }

            Rect thumb = new Rect(r.x + 1f, r.y + travel * Mathf.Clamp01(frac), r.width - 2f, thumbH - 2f);
            ScrollThumb(thumb, Mouse.IsOver(thumb));   // ★ 专用滚动滑块（AE2 官方行结构）
        }

        // =====================================================================
        //  窗口外框（含主题切换按钮）
        // =====================================================================

        /// <summary>
        /// AE2 窗口的统一外框：面板 + 标题带 + 主题切换按钮 + 关闭 ×。
        /// 各窗口只要 `bool c; Rect inner = AE2Draw.WindowFrame(r, "标题", out c);` 就自动是 AE2 的样子。
        /// </summary>
        public static Rect WindowFrame(Rect r, string title, out bool closeClicked)
        {
            bool themeClicked;
            return WindowFrame(r, title, out closeClicked, out themeClicked);
        }

        /// <summary>
        /// 同上。**主题切换按钮已删除**（用户定案：本版本只有亮色，暗色后续另起）。
        /// 保留 <paramref name="themeClicked"/> 出参只为不动调用方签名，恒为 false。
        /// </summary>
        public static Rect WindowFrame(Rect r, string title, out bool closeClicked, out bool themeClicked)
        {
            closeClicked = false;
            themeClicked = false;   // ★ 无主题可切

            PanelBox(r);
            Rect ri = r.ContractedBy(6f);

            // 标题带：凸起 + 面亮一档
            Rect head = new Rect(ri.x, ri.y, ri.width, 24f);
            BevelRaised(head, PanelFace);

            Small(new Rect(head.x + 8f, head.y + 2f, head.width - 38f, 22f), title, TextHi);

            // 关闭 ×：★ 用 AE2 按钮原语（悬停整块青，和所有按钮一致）
            Rect close = new Rect(head.xMax - 20f, head.y + 4f, 16f, 16f);
            bool closeHover = Mouse.IsOver(close);
            if (AE2TextButton(close, "×")) closeClicked = true;

            return new Rect(ri.x, head.yMax + 4f, ri.width, ri.yMax - head.yMax - 4f);
        }

        /// <summary>AE2 风格的输入框（凹陷槽 + 亮字）。</summary>
        public static string TextField(Rect r, string text)
        {
            BevelSunken(r, Slot);
            return Widgets.TextField(r.ContractedBy(3f), text);
        }

        /// <summary>AE2 风格的小按钮（凸起 + 居中文字）。active = 强调态。</summary>
        public static bool TextButton(Rect r, string label, bool active = false)
        {
            bool hover = Mouse.IsOver(r);
            if (active)
            {
                // 选中：槽位色面 + 1px 描边（AE2 的选中做法）
                Widgets.DrawBoxSolid(r, Slot);
                Outline(r);
            }
            else if (hover)
            {
                // ★ 悬停：照搬 button_highlighted.png —— 面整块变青 + 上缘白描边 + 顶底亮青 + 底阴影变青暗
                AE2Button(r, false, true);
            }
            else
            {
                AE2Button(r, false, false);
            }

            TextIn(r, label, 2f,
                   active ? TextCol : (hover ? AccentText : ButtonText), TextAnchor.MiddleCenter);
            return Widgets.ButtonInvisible(r);
        }

        /// <summary>状态小圆点（状态灯）。</summary>
        public static void StatusDot(Rect r, Color c)
        {
            float s = Mathf.Min(r.width, r.height);
            float x = r.x + (r.width - s) * 0.5f;
            float y = r.y + (r.height - s) * 0.5f;
            Widgets.DrawBoxSolid(new Rect(x + 1f, y, s - 2f, 1f), c);
            Widgets.DrawBoxSolid(new Rect(x, y + 1f, s, s - 2f), c);
            Widgets.DrawBoxSolid(new Rect(x + 1f, y + s - 1f, s - 2f, 1f), c);
        }
        // ── 径向渐变贴图（懒生成，全局一张）──────────────────────────────────────
        private static Texture2D _glowTex;
        private static Texture2D GlowTex()
        {
            if (_glowTex != null) return _glowTex;
            const int N = 64;
            _glowTex = new Texture2D(N, N, TextureFormat.ARGB32, false);
            _glowTex.wrapMode = TextureWrapMode.Clamp;
            _glowTex.filterMode = FilterMode.Bilinear;
            Color[] px = new Color[N * N];
            float half = N * 0.5f;
            for (int y = 0; y < N; y++)
            {
                for (int x = 0; x < N; x++)
                {
                    float dx = (x + 0.5f - half) / half;   // -1..1
                    float dy = (y + 0.5f - half) / half;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(1f - d);
                    a = a * a * (3f - 2f * a);             // smoothstep ⇒ 柔和的边
                    px[y * N + x] = new Color(1f, 1f, 1f, a);
                }
            }
            _glowTex.SetPixels(px);
            _glowTex.Apply();
            return _glowTex;
        }

        /// <summary>
        /// ★ 发光状态点（比汉字标签"高级"）：径向渐变的柔光核 + 亮芯，用 GUI.color 染色。
        /// 取贴图中心 60% 区域 ⇒ 边缘不切到渐变边界。
        /// </summary>
        /// <param name="state">0=等待(琥珀) 1=进行(青) 2=完成(亮青) 3=缺料(红) 4=挂起(灰) 5=阻塞(深红)</param>
        public static void GlowDot(Rect r, int state, bool pulse)
        {
            Color core;
            switch (state)
            {
                case 1: core = Accent; break;                    // 进行
                case 2: core = AccentBright; break;              // 完成
                case 3: core = Warn; break;                      // 缺料
                case 4: core = TextDimCol; break;                // 挂起
                case 5: core = Bad; break;                       // 阻塞
                default: core = Warn; break;                     // 等待
            }
            float s = Mathf.Min(r.width, r.height);
            Rect box = new Rect(r.x + (r.width - s) * 0.5f, r.y + (r.height - s) * 0.5f, s, s);

            float a = 1f;
            if (pulse && state == 1) a = 0.55f + 0.45f * Mathf.Abs(Mathf.Sin(Time.realtimeSinceStartup * 3.6f));

            Color prev = GUI.color;
            Color halo = core; halo.a = 0.55f * a;
            GUI.color = halo;
            GUI.DrawTexture(box, GlowTex(), ScaleMode.StretchToFill, true, 0f, Color.white,
                new Vector4(0.2f, 0.2f, 0.8f, 0.8f), Vector4.zero);
            float cs = s * 0.42f;
            Rect crect = new Rect(box.x + (s - cs) * 0.5f, box.y + (s - cs) * 0.5f, cs, cs);
            GUI.color = new Color(core.r, core.g, core.b, a);
            GUI.DrawTexture(crect, GlowTex(), ScaleMode.StretchToFill, true, 0f, Color.white,
                new Vector4(0.3f, 0.3f, 0.7f, 0.7f), Vector4.zero);
            GUI.color = prev;
        }

        /// <summary>带物品图标的槽位（槽框 + 居中 DefIcon）。</summary>
        public static void IconSlot(Rect r, ThingDef def, bool hover)
        {
            SlotBox(r, hover);
            if (def == null) return;
            Widgets.DefIcon(r.ContractedBy(2f), def);
        }

        // =====================================================================
        //  滚轮 / 热键 / 文字
        // =====================================================================

        /// <summary>滚轮滚一格 = 一行。maxStart = 可滚动的行数（列表条数 - 可见行数）。</summary>
        public static void WheelScroll(Rect area, ref float frac, int maxStart)
        {
            Event e = Event.current;
            if (e == null || !Mouse.IsOver(area)) return;
            if (e.type != EventType.ScrollWheel) return;
            frac = Mathf.Clamp01(frac + e.delta.y / Mathf.Max(1f, maxStart));
            e.Use();
        }

        public static void WheelScroll(Rect area, ref float frac)
        {
            Event e = Event.current;
            if (e == null || !Mouse.IsOver(area)) return;
            if (e.type != EventType.ScrollWheel) return;
            frac = Mathf.Clamp01(frac + e.delta.y * 0.06f);
            e.Use();
        }

        /// <summary>
        /// 开着我们的界面时**空格也能暂停/继续游戏**。
        /// 根因：界面里的搜索框/数量框一旦拿到键盘焦点，空格会被它当"打字"吃掉。
        /// 做法：1) 鼠标点到别处就释放输入框焦点；2) 不再自己抢暂停键（让环世界自己处理）。
        /// </summary>
        public static void HandlePauseHotkey()
        {
            Event e = Event.current;
            if (e == null) return;

            if (e.type == EventType.MouseDown && !string.IsNullOrEmpty(GUI.GetNameOfFocusedControl()))
            {
                GUI.FocusControl(null);
            }

            // ⚠️ 这里曾经用 UnityEngine.Input 直读 TimeSpeed_* 并写 Find.TickManager.CurTimeSpeed
            //    —— 那是抢原版时间控制（B8），已删除。
        }

        /// <summary>修"字体只有下半截"：Widgets.Label 会按 rect 高度裁剪，Tiny 实际需要约 17px。</summary>
        public static Rect Fit(Rect r, float minH)
        {
            if (r.height < minH) r.height = minH;
            return r;
        }

        public static void Tiny(Rect r, string s, Color c)
        {
            r = Fit(r, 17f);
            GameFont old = Verse.Text.Font; Color oc = GUI.color;
            Verse.Text.Font = GameFont.Tiny; GUI.color = c;
            Widgets.Label(r, s);
            GUI.color = oc; Verse.Text.Font = old;
        }

        public static void Small(Rect r, string s, Color c)
        {
            r = Fit(r, 21f);
            GameFont old = Verse.Text.Font; Color oc = GUI.color;
            Verse.Text.Font = GameFont.Small;
            GUI.color = c;
            Widgets.Label(r, s);
            GUI.color = oc; Verse.Text.Font = old;
        }

        // =====================================================================
        //  ★ 第 4 版新增原语 —— 严格照搬 AE2 贴图结构（逐像素实测）
        // ---------------------------------------------------------------------
        //  实测 button.png（200x20，仅 4 色，nine_slice border=3）纵向结构：
        //      y0         描边      #413F54
        //      y1         顶高光    #ADB0C4   ← 比面亮
        //      y2..h-6    面        #9A9FB4
        //      y(h-5)     底高光    #ADB0C4   ← 又亮回来（关键！）
        //      y(h-4..-2) 底阴影    #696D88   ← 是另一个色阶，不是"更暗的面"
        //      y(h-1)     描边
        //      左右两侧：**只有描边，没有高光**
        //  button_highlighted.png（hover）：面整块变青 #9CD3FF，上缘描边变白 #FFFFFF，
        //      顶/底高光 #DAFFFF，底阴影 #708CBA
        // =====================================================================

        /// <summary>
        /// ★ AE2 原版按钮：严格按 button.png 的 7 段结构画（含 hover 态）。
        /// 与 BevelRaised 的区别：左右**不画高光**、底部有"高光+阴影"夹层。
        /// </summary>
        /// <summary>
        /// ★ AE2 原版按钮：**严格 9-slice 复刻 <c>button.png</c>**（实测逐行 + mcmeta 参数）。
        ///
        /// <para>贴图 200×20，mcmeta 声明 <c>nine_slice, border: 3</c>。实测纵向 7 段（h=20）：</para>
        /// <code>
        /// y0        #413F54  描边       (border 上 3px 之内)
        /// y1        #ADB0C4  顶高光
        /// y2..14    #9A9FB4  面         13px = 20 - 3(上) - 3(下) - 1
        /// y15       #ADB0C4  底高光
        /// y16..18   #696D88  底阴影     3px  ← 9-slice 下 border 3px 里的 2px 亮 + 1px 暗
        /// y19       #413F54  描边
        /// 左右两侧：只有 #413F54 描边，**无高光**
        /// </code>
        /// <para>★ 竖向按 <c>h/20</c> 比例缩放（border 3px 随比例）——
        /// 固定写死 3px 在 h=30 的方按钮上就不对了（实测踩过）。</para>
        /// </summary>
        public static void AE2Button(Rect r, bool active, bool hover)
        {
            float x = r.x, y = r.y, w = r.width;
            int hh = Mathf.Max(8, Mathf.RoundToInt(r.height));
            float sc = hh / 20f;   // 9-slice 缩放比（贴图高 20）

            // 实测 button.png（h=20）分段：
            //   y0 描边 / y1 顶高光 / y2..14 面(13px) / y15 底高光 / y16..18 阴影(3px) / y19 描边
            //   ⇒ o1 = 描边+高光带（1px×sc）；o5 = 底部带 高光1+阴影3+描边1 = 5px×sc
            //   左/右两侧：只有描边，无高光
            int o1 = Mathf.Max(1, Mathf.RoundToInt(1f * sc));
            int o5 = Mathf.Max(5, Mathf.RoundToInt(5f * sc));    // 常态底部带：高光1+阴影3+描边1
            int o5h = Mathf.Max(4, Mathf.RoundToInt(4f * sc));   // 悬停底部带：亮青1+暗青2+描边1（实测）
            if (o1 * 2 + o5 > hh) o1 = Mathf.Max(1, (hh - o5) / 2);

            int yTopH = (int)y + o1;              // 顶高光（贴图 y1）
            int yFace = (int)y + o1 * 2;          // 面（贴图 y2）
            int yBotH = (int)y + hh - o5;         // 底高光（贴图 y15）
            int yBotS = yBotH + o1;               // 底阴影（贴图 y16..18）
            int yBotE = (int)y + hh - o1;         // 底描边（贴图 y19）
            int faceH = Mathf.Max(1, yBotH - yFace);
            int shH = Mathf.Max(1, yBotE - yBotS);

            if (hover && active)
            {
                // ★ 选中 + 悬停：保留"选中"的浅面（否则 hover 盖住 ⇒ 看不出选中了），
                //   用手绘**青色描边**做悬停反馈。这样"选中"和"悬停"两个状态都能看见。
                Widgets.DrawBoxSolid(new Rect(x + o1, yFace, w - o1 * 2f, faceH), SlotHover);
                Widgets.DrawBoxSolid(new Rect(x + o1, yTopH, w - o1 * 2f, o1), Slot);
                Widgets.DrawBoxSolid(new Rect(x + o1, yBotH, w - o1 * 2f, o1), Slot);
                Widgets.DrawBoxSolid(new Rect(x + o1, yBotS, w - o1 * 2f, shH), EdgeDeep);
                Widgets.DrawBoxSolid(new Rect(x + o1, (int)y, w - o1 * 2f, o1), OutlineCol);
                Widgets.DrawBoxSolid(new Rect(x, yTopH, o1, Mathf.Max(1, yBotE - yTopH)), OutlineCol);
                Widgets.DrawBoxSolid(new Rect(x + w - o1, yTopH, o1, Mathf.Max(1, yBotE - yTopH)), OutlineCol);
                Widgets.DrawBoxSolid(new Rect(x + o1, yBotE, w - o1 * 2f, o1), OutlineCol);
                OutlineAccentBox(new Rect(x, y, w, hh));
            }
            else if (hover)
            {
                // ★ button_highlighted.png 实测：y0 白描边 / y1 #413F54 / y2 亮青 /
                //   y3..h-5 整块青 / y(h-4) 亮青 / y(h-3..h-2) 暗青 / y(h-1) 描边
                int hbH = (int)y + hh - o5h;              // 悬停底高光（贴图 y16）
                int hbS = hbH + o1;                       // 悬停底阴影（贴图 y17,18）
                int hbE = (int)y + hh - o1;               // 悬停底描边（贴图 y19）
                Widgets.DrawBoxSolid(new Rect(x + o1, yTopH, w - o1 * 2f, Mathf.Max(1, hbH - yTopH)), BtnHoverFace);
                Widgets.DrawBoxSolid(new Rect(x + o1, yTopH, w - o1 * 2f, o1), OutlineCol);
                Widgets.DrawBoxSolid(new Rect(x + o1, yFace, w - o1 * 2f, o1), BtnHoverTop);
                Widgets.DrawBoxSolid(new Rect(x + o1, hbH, w - o1 * 2f, o1), BtnHoverTop);
                Widgets.DrawBoxSolid(new Rect(x + o1, hbS, w - o1 * 2f, Mathf.Max(1, hbE - hbS)), BtnHoverShadow);
                Widgets.DrawBoxSolid(new Rect(x + o1, (int)y, w - o1 * 2f, o1), BtnHoverEdge);
            }
            else
            {
                // ★ 选中态的面必须比高光带更亮，否则面/顶高光/底高光同色 ⇒ 立体感消失（实测踩过）
                Widgets.DrawBoxSolid(new Rect(x + o1, yFace, w - o1 * 2f, faceH), active ? SlotHover : Button);
                Widgets.DrawBoxSolid(new Rect(x + o1, yTopH, w - o1 * 2f, o1), Slot);   // 顶高光
                Widgets.DrawBoxSolid(new Rect(x + o1, yBotH, w - o1 * 2f, o1), Slot);   // 底高光（夹层）
                Widgets.DrawBoxSolid(new Rect(x + o1, yBotS, w - o1 * 2f, shH), EdgeDeep); // 底阴影
                Widgets.DrawBoxSolid(new Rect(x + o1, (int)y, w - o1 * 2f, o1), OutlineCol); // 上描边
            }

            Widgets.DrawBoxSolid(new Rect(x + o1, yBotE, w - o1 * 2f, o1), OutlineCol);      // 下描边
            Widgets.DrawBoxSolid(new Rect(x, yTopH, o1, Mathf.Max(1, yBotE - yTopH)), OutlineCol);  // 左描边
            Widgets.DrawBoxSolid(new Rect(x + w - o1, yTopH, o1, Mathf.Max(1, yBotE - yTopH)), OutlineCol); // 右描边
        }
        /// <summary>AE2 原版按钮 + 居中文字（放不下自动截断）。返回是否被点击。</summary>
        public static bool AE2TextButton(Rect r, string label, bool active = false)
        {
            bool hover = Mouse.IsOver(r);
            AE2Button(r, active, hover);
            if (!string.IsNullOrEmpty(label))
                TextIn(r, label, 2f, TextCol, TextAnchor.MiddleCenter);   // ★ 深板岩色；hover 时压亮青也够对比（6.38:1）
            return Widgets.ButtonInvisible(r);
        }

        /// <summary>
        /// ★ 文字画进容器：放不下就截断加省略号，按锚点对齐，**强制不换行**。
        /// 这是"字体溢出"的根治办法（旧版到处是固定坐标 + 固定字号写文字）。
        /// </summary>
        public static void TextIn(Rect r, string s, float pad, Color c, TextAnchor anchor)
        {
            if (string.IsNullOrEmpty(s) || r.width <= pad * 2f || r.height < 6f) return;

            GameFont oldF = Verse.Text.Font;
            TextAnchor oldA = Verse.Text.Anchor;
            Color oldC = GUI.color;
            bool oldWrap = Verse.Text.WordWrap;

            float maxW = r.width - pad * 2f;

            // ① 先按 Tiny 量宽度，放不下就截断（环世界只有 Tiny/Small/Medium 三档，不能任意缩）
            Verse.Text.Font = GameFont.Tiny;
            float needW = Verse.Text.CalcSize(s).x;
            if (needW > maxW && needW > 1f)
            {
                int keep = Mathf.Max(1, Mathf.FloorToInt(s.Length * (maxW / needW)) - 1);
                s = (keep < s.Length) ? (s.Substring(0, keep) + "…") : s;
            }
            else if (r.height >= 21f)
            {
                // ② 放得下且容器够高 ⇒ 放大一档（Small 观感更好）
                Verse.Text.Font = GameFont.Small;
                if (Verse.Text.CalcSize(s).x > maxW) Verse.Text.Font = GameFont.Tiny;
            }

            Verse.Text.Anchor = anchor;
            Verse.Text.WordWrap = false;      // ★ 不换行 ⇒ 不会撑破容器
            GUI.color = c;
            Widgets.Label(new Rect(r.x, r.y - 1f, r.width, r.height + 2f), s);
            GUI.color = oldC;
            Verse.Text.WordWrap = oldWrap;
            Verse.Text.Anchor = oldA;
            Verse.Text.Font = oldF;
        }

        /// <summary>
        /// ★ 进度条：右侧给百分比标签预留固定宽度，条子用剩余空间。
        /// 旧版把标签画在条子上/条子外面 ⇒ 溢出（实测踩过）。
        /// </summary>
        public static void ProgressBar(Rect r, float frac, Color fill, string label, bool selected)
        {
            float labelW = 0f;
            if (!string.IsNullOrEmpty(label))
            {
                labelW = Mathf.Ceil(Verse.Text.CalcSize(label).x) + 6f;
                if (labelW > r.width * 0.6f) labelW = 0f;   // 标签太宽就不画，别挤掉条子
            }

            Rect bar = new Rect(r.x, r.y, Mathf.Max(6f, r.width - labelW), r.height);
            Widgets.DrawBoxSolid(bar, BarBg);
            Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(bar.x, bar.yMax - 1f, bar.width, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, 1f, bar.height), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(bar.xMax - 1f, bar.y, 1f, bar.height), OutlineCol);

            float f = Mathf.Clamp01(frac);
            if (f > 0f)
            {
                Color c = selected ? AccentBright : ((f >= 1f) ? Accent : fill);
                Widgets.DrawBoxSolid(new Rect(bar.x + 1f, bar.y + 1f,
                    Mathf.Max(1f, (bar.width - 2f) * f), bar.height - 2f), c);
            }

            if (labelW > 0f)
                TextIn(new Rect(bar.xMax + 4f, r.y, labelW - 4f, r.height), label, 0f, TextCol, TextAnchor.MiddleLeft);
        }

        /// <summary>
        /// ★ 多段式方按钮（AE2 原版都是并列方按钮，不是循环切换）。
        /// 返回被点中的下标；没点返回 -1。
        /// </summary>
        public static int Segmented(Rect r, string[] labels, int selected, float gap)
        {
            if (labels == null || labels.Length == 0) return -1;
            float w = (r.width - gap * (labels.Length - 1)) / labels.Length;
            int hit = -1;
            for (int i = 0; i < labels.Length; i++)
            {
                Rect b = new Rect(r.x + i * (w + gap), r.y, w, r.height);
                if (AE2TextButton(b, labels[i], i == selected)) hit = i;
            }
            return hit;
        }

        /// <summary>★ 凹槽容器（照搬 vertical_buttons_bg.png：左 1px 高光 + 面 + 底高光 1px + 底阴影 2px）。</summary>
        public static void SunkenBox(Rect r)
        {
            Widgets.DrawBoxSolid(r, PanelFace);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, 1f, r.height), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(r.xMax - 1f, r.y, 1f, r.height), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(r.x + 1f, r.y + 1f, 1f, r.height - 2f), EdgeHi);
            Widgets.DrawBoxSolid(new Rect(r.x + 1f, r.yMax - 3f, r.width - 2f, 1f), EdgeHi);
            Widgets.DrawBoxSolid(new Rect(r.x + 1f, r.yMax - 2f, r.width - 2f, 2f), LineSoft);
        }

        // =====================================================================
        //  ★ 新骨架部件（按模板：窗口底 / 栏头 / 平栏）
        // =====================================================================

        /// <summary>窗口整个背景（AE2 无外框样式：亮底 + 1px 描边 + 上缘高光）。</summary>
        public static void WindowBg(Rect r)
        {
            Widgets.DrawBoxSolid(r, Panel);
            Widgets.DrawBoxSolid(new Rect(r.x + 1f, r.y + 1f, r.width - 2f, 1f), EdgeHi);
            Outline(r);
        }

        /// <summary>栏头（标题栏）：亮底平色 + 下缘细线（照抄 terminal.png 的 header 段结构）。</summary>
        public static void ColumnHead(Rect r)
        {
            Widgets.DrawBoxSolid(r, Panel);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(r.x + 1f, r.y + 1f, r.width - 2f, 1f), EdgeHi);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), LineSoft);
        }

        /// <summary>
        /// 内容栏（照模板实测结构）：<c>#CBCCD4</c> 亮底 + 1px 描边 + **头部一条 <c>#9A9FB4</c> 带**。
        ///
        /// <para>模板竖向剖面（栏1 顶部）：</para>
        /// <list type="bullet">
        /// <item>y0     <c>#F2F2F2</c> 头带亮线</item>
        /// <item>y1..6  <c>#9A9FB4</c> 头带（按钮面色）</item>
        /// <item>y7     <c>#696D88</c> 头带下缘暗线</item>
        /// <item>y8..   <c>#CBCCD4</c> 内容区（亮）</item>
        /// </list>
        /// <para>★ 这个"头带 + 内容区"的双色结构就是**界面的分界**（旧版全同色 ⇒ 看不出分栏）。</para>
        /// </summary>
        public static void FlatColumn(Rect r)
        {
            Widgets.DrawBoxSolid(r, Panel);                                        // 内容区亮底
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 24f), Button);        // ★ 头带 24px（原来 8px 装不住 Tiny 的 17px 文字）
            Widgets.DrawBoxSolid(new Rect(r.x + 1f, r.y + 1f, r.width - 2f, 1f), EdgeHi); // 头带亮线
            Widgets.DrawBoxSolid(new Rect(r.x, r.y + 23f, r.width, 1f), EdgeLo);   // 头带下缘暗线
            Outline(r);                                                            // 1px 描边（栏边界）
        }

        /// <summary>用任意色 1px 描边（节点状态色等）。</summary>
        public static void OutlineColor(Rect r, Color c)
        {
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), c);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), c);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, 1f, r.height), c);
            Widgets.DrawBoxSolid(new Rect(r.xMax - 1f, r.y, 1f, r.height), c);
        }

        /// <summary>
        /// 列表行（**照目标图**）：1px 描边 + **平面填充**，没有按钮的顶高光/底阴影带。
        ///
        /// <para>为什么不用 AE2Button：按钮的顶高光(1px)+底阴影(按比例) 会吃掉行高 ⇒
        /// 行高 18~32px 时文字被挤到看不见（实测踩过）。目标图的列表行就是"细描边 + 平面"。</para>
        /// </summary>
        public static void FlatRow(Rect r, bool hover, bool selected)
        {
            Widgets.DrawBoxSolid(r, selected ? Slot : (hover ? ButtonHover : Panel));
            if (selected) OutlineAccentBox(r);
            else Outline(r);
        }
        /// <summary>
        /// ★ AE2 同款容量/进度条（照 <c>vertical_buttons_bg.png</c> 凹槽 + 强调色填充）：
        /// 深槽 <c>#878FA5</c> + 1px <c>#413F54</c> 描边 + 填充 + 可选右侧百分比标签。
        /// </summary>
        /// <param name="fill">填充色（进度=Accent 青 / 容量=Warn 橙）</param>
        /// <param name="label">右侧标签（null 则不画）</param>
        public static void StateBar(Rect r, float frac, Color fill, string label)
        {
            float lw = 0f;
            if (!string.IsNullOrEmpty(label))
            {
                lw = Mathf.Ceil(Verse.Text.CalcSize(label).x) + 6f;
                if (lw > r.width * 0.5f) lw = 0f;
            }
            Rect bar = new Rect(r.x, r.y, Mathf.Max(8f, r.width - lw), r.height);
            Widgets.DrawBoxSolid(bar, BarBg);
            Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(bar.x, bar.yMax - 1f, bar.width, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, 1f, bar.height), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(bar.xMax - 1f, bar.y, 1f, bar.height), OutlineCol);
            float f = Mathf.Clamp01(frac);
            if (f > 0f && bar.width > 2f && bar.height > 2f)
                Widgets.DrawBoxSolid(new Rect(bar.x + 1f, bar.y + 1f,
                    Mathf.Max(1f, (bar.width - 2f) * f), bar.height - 2f), fill);
            if (lw > 0f)
                TextIn(new Rect(bar.xMax + 4f, r.y, lw - 4f, r.height), label, 0f, TextCol, TextAnchor.MiddleLeft);
        }
    }
}
