// =====================================================================================
//  【本地新增文件】AE2Draw —— AE2 风格的共用绘制库（目标③"统一材质"的地基）
// -------------------------------------------------------------------------------------
//  为什么单独一个文件：制作面板、加配方窗、下单窗、状态窗……都要画同一套东西。
//  本文件的画法与配色**全部来自 AE2 官方图集**（terminal.png / craftingcpu.png /
//  states.png）逐像素采样，见 AE2-界面复刻规格.md：
//      · 面板/按钮 = **凸起**（亮边在上/左，暗边在下/右）+ 1px 黑描边
//      · 槽位      = **内凹**（暗边在上/左，亮边在下/右）
//      · 全部用 1px 矩形拼（AE2 的做法）⇒ 任意分辨率都清晰，不依赖贴图
//
//  ⚠️ 硬约束（踩过的坑）：
//    1) 自绘窗口**不要** BeginScrollView / BeginClip（不成对 ⇒ GUI 栈失衡 ⇒ 整个界面点不动）；
//       滚动一律用本文件的 DragBar（自绘 + 自己处理鼠标事件）。
//    2) C# 7.3 不接受 Color32 与 Color 混用的三元表达式（CS8957）⇒ 颜色一律用 Color 字段。
// =====================================================================================
using UnityEngine;
using Verse;
using RimWorld;   // ★ KeyBindingDefOf（暂停键绑定）在这个命名空间

namespace DigitalStorage.UI
{
    internal static class AE2Draw
    {
        // ---- 配色（采样自 AE2 图集；改这里就是"换材质"）----
        public static readonly Color Panel = new Color32(0xC6, 0xC6, 0xC6, 0xFF);
        public static readonly Color Button = new Color32(0xDF, 0xDF, 0xDF, 0xFF);
        public static readonly Color ButtonHover = new Color32(0xEC, 0xEC, 0xEC, 0xFF);
        public static readonly Color Slot = new Color32(0x8B, 0x8B, 0x8B, 0xFF);
        public static readonly Color SlotHover = new Color32(0xA8, 0xA8, 0xA8, 0xFF);
        public static readonly Color Hi = new Color32(0xFF, 0xFF, 0xFF, 0xFF);
        public static readonly Color Lo = new Color32(0x37, 0x37, 0x37, 0xFF);
        public static readonly Color OutlineCol = new Color32(0x00, 0x00, 0x00, 0xFF);
        public static readonly Color Accent = new Color32(0x00, 0x77, 0xA5, 0xFF);
        public static readonly Color Warn = new Color32(0xD9, 0xA0, 0x2B, 0xFF);
        public static readonly Color Bad = new Color32(0xB0, 0x2E, 0x26, 0xFF);
        public static readonly Color TextCol = new Color32(0x40, 0x40, 0x40, 0xFF);   // ⚠️ 不能叫 Text：会遮蔽 Verse.Text
        public static readonly Color TextDimCol = new Color32(0x5A, 0x5A, 0x5A, 0xFF);
        public static readonly Color BarBg = new Color32(0x6E, 0x6E, 0x6E, 0xFF);

        /// <summary>AE2 的面板：凸起 + 1px 黑描边。</summary>
        public static void PanelBox(Rect r)
        {
            Widgets.DrawBoxSolid(r, Panel);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 2f), Hi);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, 2f, r.height), Hi);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 2f, r.width, 2f), Lo);
            Widgets.DrawBoxSolid(new Rect(r.xMax - 2f, r.y, 2f, r.height), Lo);
            Outline(r);
        }

        /// <summary>槽位：内凹。</summary>
        public static void SlotBox(Rect r, bool hover)
        {
            Widgets.DrawBoxSolid(r, hover ? SlotHover : Slot);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), Lo);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, 1f, r.height), Lo);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), Hi);
            Widgets.DrawBoxSolid(new Rect(r.xMax - 1f, r.y, 1f, r.height), Hi);
        }

        /// <summary>凹槽（进度条底 / 标题带）。</summary>
        public static void Sunken(Rect r, Color fill)
        {
            Widgets.DrawBoxSolid(r, fill);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), Lo);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, 1f, r.height), Lo);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), Hi);
            Widgets.DrawBoxSolid(new Rect(r.xMax - 1f, r.y, 1f, r.height), Hi);
        }

        /// <summary>按钮：凸起（active = 青色激活态）。</summary>
        public static void FlatButton(Rect r, bool active)
        {
            Widgets.DrawBoxSolid(r, active ? Accent : Button);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), Hi);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, 1f, r.height), Hi);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), Lo);
            Widgets.DrawBoxSolid(new Rect(r.xMax - 1f, r.y, 1f, r.height), Lo);
        }

        /// <summary>列表行：常态/悬停/选中三态（AE2 的 CPU 行就是选中青底白字）。</summary>
        public static void Row(Rect r, bool hover, bool selected)
        {
            Widgets.DrawBoxSolid(r, selected ? Accent : (hover ? ButtonHover : Button));
            if (!selected)
            {
                Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), Hi);
                Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), Lo);
            }
            Outline(r);
        }

        public static void Outline(Rect r)
        {
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, 1f, r.height), OutlineCol);
            Widgets.DrawBoxSolid(new Rect(r.xMax - 1f, r.y, 1f, r.height), OutlineCol);
        }

        /// <summary>进度/容量条：底槽 + 填充（满 = 青，未满 = 琥珀，超限 = 红）。</summary>
        public static void Bar(Rect r, float frac, bool selected)
        {
            Sunken(r, BarBg);
            float f = Mathf.Clamp01(frac);
            if (f <= 0f) return;
            Color c = selected ? Hi : ((f >= 1f) ? Accent : Warn);
            Widgets.DrawBoxSolid(new Rect(r.x + 1f, r.y + 1f, Mathf.Max(1f, (r.width - 2f) * f), r.height - 2f), c);
        }

        /// <summary>
        /// AE2 那种**侧面可拖动的滚动条**：按住滑块拖 / 点轨道跳 / 滚轮滚，都会改 frac（0~1）。
        /// 滑块长度 = 可见比例 viewPct。**不用 BeginScrollView**（见文件头约束 1）。
        /// </summary>
        public static void DragBar(Rect r, ref float frac, float viewPct)
        {
            Sunken(r, BarBg);
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
            Widgets.DrawBoxSolid(thumb, Button);
            Widgets.DrawBoxSolid(new Rect(thumb.x, thumb.y, thumb.width, 1f), Hi);
            Widgets.DrawBoxSolid(new Rect(thumb.x, thumb.yMax - 1f, thumb.width, 1f), Lo);
            Outline(thumb);
        }

        /// <summary>
        /// ★ 目标③：AE2 窗口的**统一外框** —— 面板 + 标题带 + 面板内右上角的 ×。
        /// 各窗口只要 `bool close; Rect inner = AE2Draw.WindowFrame(r, "标题", out close);`
        /// 就自动是 AE2 的样子。**改配色/边框只改本文件** —— 这就是"统一材质"的落地点。
        /// </summary>
        public static Rect WindowFrame(Rect r, string title, out bool closeClicked)
        {
            closeClicked = false;
            PanelBox(r);
            Rect ri = r.ContractedBy(6f);
            Rect head = new Rect(ri.x, ri.y, ri.width, 22f);
            Sunken(head, BarBg);
            Tiny(new Rect(head.x + 4f, head.y + 3f, head.width - 26f, 16f), title, Hi);
            Rect close = new Rect(head.xMax - 18f, head.y + 3f, 15f, 15f);
            if (Widgets.ButtonInvisible(close)) closeClicked = true;
            FlatButton(close, Mouse.IsOver(close));
            // ★ × 居中：用 Text.Anchor 画在按钮框正中间（之前 Tiny 的最小高度把它挤偏了）
            TextAnchor __oldAnchor = Verse.Text.Anchor;
            Verse.Text.Anchor = TextAnchor.MiddleCenter;
            Verse.Text.Font = GameFont.Tiny;   // × 用 Tiny，正好放进 15px 的按钮
            GUI.color = AE2Draw.TextCol;
            Widgets.Label(new Rect(close.x, close.y - 3f, close.width, close.height + 5f), "×");   // 上下各放一点，垂直居中且不被裁
            GUI.color = Color.white;
            Verse.Text.Anchor = __oldAnchor;
            return new Rect(ri.x, head.yMax + 4f, ri.width, ri.yMax - head.yMax - 4f);
        }

        /// <summary>★ 目标③：AE2 风格的输入框（深灰凹槽 + 亮字）。</summary>
        public static string TextField(Rect r, string text)
        {
            Sunken(r, Slot);
            return Widgets.TextField(r.ContractedBy(3f), text);
        }

        /// <summary>★ 目标③：AE2 风格的小按钮（带文字）。</summary>
        public static bool TextButton(Rect r, string label, bool active = false)
        {
            bool hover = Mouse.IsOver(r);
            FlatButton(r, active || hover);
            Tiny(new Rect(r.x + 4f, r.y + 3f, r.width - 8f, 14f), label, active ? Hi : TextCol);
            return Widgets.ButtonInvisible(r);
        }
        /// <summary>
        /// ★ 用户要求：鼠标**在列表区域内**滚滚轮也要能滚（原来只有按住右侧滚动条拖才行 ——
        /// 滚轮判断挂在细条 rect 上，指针在列表里时不算"悬停滚动条"）。各列表画完后调一次。
        /// </summary>
        /// <summary>★ 用户要求：滚轮滚一格 = 一行。maxStart = 可滚动的行数（列表条数 - 可见行数）。</summary>
        public static void WheelScroll(Rect area, ref float frac, int maxStart)
        {
            Event e = Event.current;
            if (e == null || !Mouse.IsOver(area)) return;
            if (e.type != EventType.ScrollWheel) return;
            frac = Mathf.Clamp01(frac + e.delta.y / Mathf.Max(1f, maxStart));   // 一格 = 一行
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
        /// ★ 用户要求：开着我们的界面时，**空格也能暂停/继续游戏**。
        /// 根因：界面里的搜索框/数量框一旦拿到键盘焦点，空格会被它当"打字"吃掉（Event.Use()），
        ///       永远到不了游戏的 TogglePause 键绑定 ⇒ 空格像失灵。
        /// 做法：
        ///   1) 鼠标点到别处就**释放输入框焦点**（否则焦点会一直黏在搜索框上）；
        ///   2) 只有在**没有输入框聚焦**时，才自己接管 TogglePause 并吃掉这次事件。
        /// 各窗口/页签在绘制开头调一次即可。
        /// </summary>
        public static void HandlePauseHotkey()
        {
            Event e = Event.current;
            if (e == null) return;

            // 1) 点到别处 ⇒ 释放焦点（点进输入框的那一下不受影响：本方法在 TextField 之前跑）
            if (e.type == EventType.MouseDown && !string.IsNullOrEmpty(GUI.GetNameOfFocusedControl()))
            {
                GUI.FocusControl(null);
            }

            // 2) 暂停键：用 Unity Input **直接读**，不依赖 GUI 事件（GUI 事件可能已被 TextField 吃掉，
            //    或被环世界自己置成 Used ⇒ 之前那种判断常常不成立，空格就像失灵）。
            //    只在"有窗口在吸收输入"时接管：那种状态下环世界本来就会跳过全局按键，
            //    所以不会和它重复触发（重复会互相抵消：切一次又切回来 ⇒ 看起来没反应）。
            //    ★ 注意：打开我们的窗口时，环世界会**跳过它自己的全局按键处理**（因为它认为窗口在吸收输入），
            //      所以暂停键、加速键（1/2/3）都得我们自己接管 —— 否则玩家在面板里按什么都没反应。
            //      （本方法只在"我们的窗口正在绘制"时被调用，所以不会与环世界的处理重复触发。）
            if (!string.IsNullOrEmpty(GUI.GetNameOfFocusedControl())) return;   // 正在输入框里打字 ⇒ 不抢键

            // ★ 结论：不再自己抢暂停/加速键 —— 之前"我抢 + 环世界也在处理"会互相抵消（切一次又切回来，
            //   看起来就是按了没反应）。正解是**让环世界自己去处理**：
            //   把窗口的 absorbInputAroundWindow 关掉（见各窗口的 InitWindow），环世界就不会跳过全局按键了。        }
            //    （键绑定的真实 defName 取自原版 Data\Core\Defs\KeyBindings.xml）
            if (UnityEngine.Input.GetKeyDown(KeyBindingDefOf.TimeSpeed_Normal.MainKey))
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                return;
            }
            if (UnityEngine.Input.GetKeyDown(KeyBindingDefOf.TimeSpeed_Fast.MainKey))
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Fast;
                return;
            }
            if (UnityEngine.Input.GetKeyDown(KeyBindingDefOf.TimeSpeed_Ultrafast.MainKey))
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Ultrafast;
                return;
            }
            if (UnityEngine.Input.GetKeyDown(KeyBindingDefOf.TimeSpeed_Superfast.MainKey))
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
            }
        }
        /// <summary>★ 修"字体只有下半截"：Widgets.Label 会按 rect 高度裁剪，Tiny 实际需要约 17px。</summary>
        public static Rect Fit(Rect r, float minH)
        {
            if (r.height < minH) r.height = minH;
            return r;
        }

        public static void Tiny(Rect r, string s, Color c)
        {
            r = Fit(r, 17f);   // ★ 太小就抬到 17px，否则下半截被裁掉
            GameFont old = Verse.Text.Font; Color oc = GUI.color;
            Verse.Text.Font = GameFont.Tiny; GUI.color = c;
            Widgets.Label(r, s);
            GUI.color = oc; Verse.Text.Font = old;
        }

        public static void Small(Rect r, string s, Color c)
        {
            r = Fit(r, 21f);   // ★ Small 实际需要约 21px
            GameFont old = Verse.Text.Font; Color oc = GUI.color;
            Verse.Text.Font = GameFont.Small;
            GUI.color = c;
            Widgets.Label(r, s);
            GUI.color = oc; Verse.Text.Font = old;
        }
    }
}
