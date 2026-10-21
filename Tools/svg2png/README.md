# svg2png —— 图标源文件（SVG）与转换脚本

本 mod 的 **gizmo / 按钮图标**在这里画、在这里转。图标不用绘图软件产出：源文件是手写 SVG
（纯文本、可 diff、改一像素不用开 PS），用 `svg2png.py` 转成 RimWorld 要的 PNG。

## 为什么自带一个光栅器

本机没有 cairosvg / inkscape / rsvg（cairosvg 在 Windows 还要额外拖 cairo DLL），
而图标只需要 SVG 的一小个子集 ⇒ 与其装工具链，不如用 **Pillow + numpy** 把子集吃干
（脚本 ~380 行，零额外依赖）。

## 用法

```powershell
# 单个
python Tools/svg2png/svg2png.py Tools/svg2png/icons/制作代理-开关.svg -o Textures/UI/Gizmos/制作代理-开关.png --size 128

# 整个目录 + 出一张"原版按钮实测"预览图
python Tools/svg2png/svg2png.py Tools/svg2png/icons -o Tools/svg2png/out --size 128 `
       --preview Tools/svg2png/preview.png
```

**每次改完 SVG 都要**：转 PNG → 把 `Tools/svg2png/out/*.png` 覆盖到 `Textures/UI/Gizmos/`
→ 复制到游戏 mod 目录（`Textures` 不在 `CLAUDE.md` 那份"改哪个复制哪个"的清单里，但**必须一起复制**）。

## SVG 子集（超出的写法会**明确报错**，不会静默画错）

| 支持 | 说明 |
|---|---|
| 元素 | `<svg width height viewBox>`、`<g>`（属性继承）、`<path>`、`<circle>`、`<rect rx>`、`<polygon>`、`<polyline>`、`<line>` |
| 路径指令 | `M L H V C Q A Z`（大小写 = 绝对 / 相对；`A` 是完整端点式圆弧，规范 F.6.5） |
| 属性 | `fill` `fill-opacity` `stroke` `stroke-width` `stroke-opacity` `stroke-linecap` `opacity`（也可写进 `style="…"`） |
| 颜色 | `#rgb` `#rrggbb` `#rrggbbaa` `rgb()/rgba()` `none` |
| **不支持** | `transform`、渐变、`text`、`clipPath`、`mask`、`filter`、`stroke-dasharray`、`S`/`T` 简写 —— 要这些请装 cairosvg |

画法（三条都关系到"看起来对"）：
1. **4× 超采样**后 LANCZOS 降采样 = 抗锯齿（脚本是"直接把坐标乘 4 画上去"，不是先画小再放大 —— 后者会二次模糊）；
2. **描边 = 沿折线连续盖圆盘** ⇒ 圆头/圆拐角天然正确，且严格**骑在路径上**（Pillow 的 `outline=,width=` 是往里画，会差半个线宽）；
3. 每个图元先渲成 L 掩膜再按 source-over 合成 ⇒ 半透明图元自重叠不会局部变深。

## RimWorld 侧的几条硬规矩（踩过）

1. **gizmo 不给图标 = 顶一个"坏贴图"占位**：`Command.DrawIcon` 在 `icon == null` 时画
   `BaseContent.BadTex`（`Command.cs:251-257`），不是"只显示文字"。所以每个 gizmo 都要给图。
2. 贴图路径 = **相对 `Textures/` 且不带扩展名**：`ContentFinder<Texture2D>.Get("UI/Gizmos/制作代理-开关", true)`；
   第二个参数 `true` = 找不到就报错（**别用默认的静默失败**，那是"图标不见了但日志干净"的来源）。
3. **尺寸 128×128、透明底**：原版按 `iconDrawScale × 0.85` 画进 75×75 的按钮，128 够清晰又不浪费。
   图标自带 ~8% 外边距，所以实际着色区约等于按钮的 70%。
4. **右上角 24×24 是 `Command_Toggle` 的复选框位置**（`Command_Toggle.cs:44-47`）：
   开关类图标别把"关键笔画"放那儿 —— `preview.png` 会把它叠上来给你看。
5. 图标配色要能在原版深灰按钮上读出来（本 mod 用：近白 = 开关、琥珀 = 超频、青 = 范围）。

## 现有图标

| SVG | 用在哪 | motif |
|---|---|---|
| `制作代理-开关.svg` | `CompBillAutomation` 总开关 gizmo | 电源符号（断口圆环 + 竖杠），近白 |
| `制作代理-超频.svg` | 超频档位 gizmo | 速度表（开口表盘 + 指针 + 轴心 + 刻度），琥珀 |
| `制作代理-显示范围.svg` | 显示扫描范围 gizmo | 方框 + 四向刻度 + 中心块，青 |
| `制作代理-并行.svg` | 每步并行上限 gizmo | 三条并排台子 + 顶部上限横杠，绿 |

`preview.png` 是"原版按钮实测图"（128 / 64 / 32 px + 复选框叠加），改完图标先看它，
比进游戏试快得多。
