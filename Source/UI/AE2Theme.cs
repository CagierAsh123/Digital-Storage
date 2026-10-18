// =====================================================================================
//  【本地新增文件】AE2Theme —— AE2 官方配色表（**单套亮色，无主题切换**）
// -------------------------------------------------------------------------------------
//  ★ 用户定案（当前版本）：**只保留亮色**，以 AE2 官方界面为唯一模板。
//     暗色表已按用户要求**删除**；后续要加暗色，另起一套并保留本表为基准。
//
//  ★ 色值来源（不自己估，全部逐像素采样）：
//      · appliedenergistics2-26.3.1-alpha.jar 的
//          assets/ae2/textures/guis/{background,terminal,crafting,craftingcpu}.png
//          assets/ae2/textures/gui/sprites/{button,button_highlighted,big_scroller,vertical_buttons_bg}.png
//          assets/ae2/screens/common/palette.json
//      · 用户提供的 AE2 真机截图（样板管理终端）
//    实测整个 AE2 界面**只用 7 个基础色**（这是它观感统一的关键）：
//      #413F54 描边/文字    #F2F2F2 高光        #CBCCD4 背景/外框
//      #ADB0C4 槽位底/顶高光  #9A9FB4 内容区/按钮面  #696D88 底阴影/深槽  #878FA5 次要/线
//    强调色 #9CD3FF（cpu_selector），hover 面同色；选中底 #ACE9FF（palette.json）。
//
//  ★ 面积配比（AE2 官方终端 195x170 实测，作为"像不像"的量化验收标准）：
//      #ADB0C4 45.6% / #CBCCD4 40.7% / #F2F2F2 6.2% / #9A9FB4 3.6% / #413F54 2.5%
//
//  ⚠️ 立体感按贴图逐行结构还原（见 AE2Draw.AE2Button 的 7 段注释）。
// =====================================================================================
using UnityEngine;

namespace DigitalStorage.UI
{
    /// <summary>一套配色的全部色值。</summary>
    internal sealed class AE2Palette
    {
        // ---- 面 / 层次 ----
        public Color Bg;            // 窗口外的底
        public Color Panel;         // 面板面
        public Color PanelFace;     // 面板的"更亮一档"（标题带 / 详情头 / 凹槽容器面）
        public Color Button;        // 控件面
        public Color ButtonHover;   // 控件悬停
        public Color Slot;          // 槽位面
        public Color SlotHover;
        public Color BarBg;         // 深槽（进度条底）

        // ---- 按钮 hover 态（★ 取自 AE2 button_highlighted.png 实测）----
        public Color BtnHoverFace;  // 悬停时的面（AE2 是整块变青 #9CD3FF）
        public Color BtnHoverTop;   // 悬停时的顶/底高光（#DAFFFF）
        public Color BtnHoverShadow;// 悬停时的底阴影（#708CBA）
        public Color BtnHoverEdge;  // 悬停时上缘的线（贴图是 #FFFFFF，本项目改成与面同色避免突兀）

        // ---- 立体边 ----
        public Color EdgeHi;        // 高光边（槽位外圈）
        public Color EdgeLo;        // 阴影边
        public Color EdgeDeep;      // 更深的阴影（按钮底 3px，实测 #696D88）
        public Color OutlineCol;    // 1px 描边（实测 #413F54）
        public Color LineSoft;      // 内部细线（实测 #878FA5）

        // ---- 文字 ----
        public Color TextCol;       // 正文
        public Color TextDimCol;    // 次要
        public Color TextHi;        // 强调文字 / 标题
        public Color ButtonText;    // 落在**控件面**上的正文
        public Color ButtonTextDim; // 落在控件面上的次要文字

        // ---- 语义 ----
        public Color Accent;        // 强调（AE2 淡青）
        public Color AccentBright;  // 强调亮
        public Color AccentDim;     // 强调底（选中行填充）
        public Color AccentText;    // 强调色**当文字**时用的版本
        public Color Warn;
        public Color Bad;
        public Color Ok;
    }

    internal static class AE2Theme
    {
        /// <summary>永远 false —— 本版本只有亮色。留着是为了不动调用方签名。</summary>
        public static bool IsDark { get { return false; } }

        public static AE2Palette Active { get { return Light; } }

        // 颜色只能写成 Color32(...) 常量式，不能引用别的字段（初始化顺序陷阱）
        public static readonly AE2Palette Light = new AE2Palette
        {
            // ---- 面 / 层次（★ 全部实测）----
            Bg          = new Color32(0xCB, 0xCC, 0xD4, 0xFF),   // ★ 窗口外底（与 Panel 同色，窗口无外框）
            Panel       = new Color32(0xCB, 0xCC, 0xD4, 0xFF),   // ★ background.png 主面（占 40.7%）
            PanelFace   = new Color32(0xCB, 0xCC, 0xD4, 0xFF),   // ★ 凹槽容器面（vertical_buttons_bg.png）
            Button      = new Color32(0x9A, 0x9F, 0xB4, 0xFF),   // ★ button.png 面（占 18%）
            ButtonHover = new Color32(0xAD, 0xB0, 0xC4, 0xFF),   // 悬停亮一档
            Slot        = new Color32(0xAD, 0xB0, 0xC4, 0xFF),   // ★ 槽位底（占 5.8%）
            SlotHover   = new Color32(0xCB, 0xCC, 0xD4, 0xFF),
            BarBg       = new Color32(0x87, 0x8F, 0xA5, 0xFF),   // ★ 深槽（progress 底）—— 比 #CBCCD4 深，青色填充才看得见（1.3:1→2.0:1）

            // ---- 按钮 hover 态（★ button_highlighted.png 实测）----
            BtnHoverFace   = new Color32(0x9C, 0xD3, 0xFF, 0xFF),   // 面整块变青
            BtnHoverTop    = new Color32(0xDA, 0xFF, 0xFF, 0xFF),   // 顶/底高光
            BtnHoverShadow = new Color32(0x70, 0x8C, 0xBA, 0xFF),   // 底阴影
            BtnHoverEdge   = new Color32(0x9C, 0xD3, 0xFF, 0xFF),   // ★ 原贴图是纯白，但环世界下白线太突兀 ⇒ 改成与面同色

            // ---- 立体边（★ button.png / terminal.png 实测）----
            EdgeHi      = new Color32(0xF2, 0xF2, 0xF2, 0xFF),   // ★ 槽位外圈高光（占 1.5%）
            EdgeLo      = new Color32(0x69, 0x6D, 0x88, 0xFF),   // ★ 按钮底阴影
            EdgeDeep    = new Color32(0x69, 0x6D, 0x88, 0xFF),   // ★ 按钮底阴影 3px
            OutlineCol  = new Color32(0x41, 0x3F, 0x54, 0xFF),   // ★ 描边（占 5.6%）
            LineSoft    = new Color32(0x87, 0x8F, 0xA5, 0xFF),   // ★ 内部细线

            // ---- 文字（★ palette.json DEFAULT_TEXT_COLOR / MUTED_TEXT_COLOR）----
            //   注：#413F54 在 #9A9FB4 面上只有 3.87:1 —— 官方就是这样（不是 bug），保持原样最"像"。
            TextCol     = new Color32(0x41, 0x3F, 0x54, 0xFF),
            TextDimCol  = new Color32(0x87, 0x8F, 0xA5, 0xFF),
            TextHi      = new Color32(0x41, 0x3F, 0x54, 0xFF),
            ButtonText    = new Color32(0x41, 0x3F, 0x54, 0xFF),
            ButtonTextDim = new Color32(0x59, 0x5E, 0x6D, 0xFF),

            // ---- 语义 ----
            Accent        = new Color32(0x9C, 0xD3, 0xFF, 0xFF),   // ★ cpu_selector.png
            AccentBright  = new Color32(0xDA, 0xFF, 0xFF, 0xFF),
            AccentDim     = new Color32(0x9C, 0xD3, 0xFF, 0xFF),
            AccentText    = new Color32(0x16, 0x3B, 0x4E, 0xFF),   // 强调当文字（在青底上可读）
            Warn          = new Color32(0xE8, 0xB4, 0x58, 0xFF),
            Bad           = new Color32(0xB0, 0x2E, 0x26, 0xFF),
            Ok            = new Color32(0x28, 0x5A, 0x3A, 0xFF),
        };
    }
}
