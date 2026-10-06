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

        private const float HeaderH = 24f;
        private const float InfoH = 18f;
        private const float FilterH = 22f;
        private const float RowH = 24f;
        private const float CoreRowH = 20f;

        public Window_AE2CraftPanel(CompBillAutomation comp)
        {
            this.comp = comp;
            this.doCloseX = false;
            this.closeOnClickedOutside = false;
            this.absorbInputAroundWindow = false;   // ★ 让环世界继续处理全局按键（空格/加速/摄像机）
            this.onlyOneOfTypeAllowed = true;
            this.draggable = true;
            this.resizeable = true;   // ★ 第 11 轮：可缩放（布局全按 rect 算，能自适应）
            this.doWindowBackground = false;
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
                    AE2Draw.Tiny(new Rect(r0.x + 6f, r0.y + 6f, r0.width - 12f, 20f),
                        "DS_CA_NoProxyHint".Translate().ToString(), AE2Draw.TextCol);
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
                AE2Draw.Tiny(new Rect(rs.x + 6f, rs.y + 6f, rs.width - 12f, 20f),
                    "DS_AE2_TooSmall".Translate().ToString(), AE2Draw.Bad);
                return;
            }
            if (map == null) { Close(); return; }

            bool closeClicked;
            string title = comp.parent.LabelShort.ToString() + "　" + comp.parent.Position.ToString();
            Rect ri = AE2Draw.WindowFrame(inRect.ContractedBy(2f), title, out closeClicked);

                // ★ 用户要求：点标题栏可**切换其它制作代理**（不必关窗再点建筑）
                Rect titleR = new Rect(ri.x, ri.y - 22f, ri.width - 26f, 20f);
                if (Widgets.ButtonInvisible(titleR)) OpenProxyMenu();
            if (closeClicked) { Close(); return; }

            IList<CraftPlan> plans = comp.PlansForReading;
            if (plans == null) plans = new List<CraftPlan>();
            if (sel != null && !plans.Contains(sel)) sel = null;
            if (sel == null && plans.Count > 0) sel = plans[0];

            float y = ri.y;
            y = DrawTopBar(ri, y);
            y = DrawFilterBar(ri, y, plans);

            float listH = ri.yMax - y;
            float coreW = 196f;
            float orderW = 246f;
            Rect coreCol = new Rect(ri.x, y, coreW, listH);
            Rect orderCol = new Rect(coreCol.xMax + 6f, y, orderW, listH);
            Rect detailCol = new Rect(orderCol.xMax + 6f, y, ri.xMax - orderCol.xMax - 6f, listH);

            DrawCore(coreCol, map);
            DrawOrders(orderCol, plans);
            DrawDetail(detailCol, map);
            }
            catch (Exception __uiEx) { Log.ErrorOnce("[DigitalStorage] AE2 界面绘制异常（只记一次，界面不会卡死）：" + __uiEx, 771001); }
        }

        // ---- 顶栏：开关 / 超频 / 添加配方 + 耗电与统计 ----
        private float DrawTopBar(Rect ri, float y)
        {
            Rect row = new Rect(ri.x, y, ri.width, HeaderH);
            string onOff = comp.Enabled ? "DS_BA_On".Translate().ToString() : "DS_BA_Off".Translate().ToString();
            float bw = 152f;   // D12：收窄，腾位置给「存储核心」

            // D12：一键选中数字存储核心（它的检查面板就是存储页签）
            if (AE2Draw.TextButton(new Rect(row.x + 2f * (bw + 4f), row.y, 96f, HeaderH), "DS_AE2_ToCore".Translate().ToString()))
            {
                Building_StorageCore core = FirstCore(comp.parent.Map);
                if (core != null) { Find.Selector.ClearSelection(); Find.Selector.Select(core, false, false); Close(); }
            }

            // ★ 合成树：对当前选中的订单展开整条依赖链（AE2 的合成树）
            if (AE2Draw.TextButton(new Rect(row.x + 2f * (bw + 4f) + 100f, row.y, 88f, HeaderH), "DS_AE2_Tree_Btn".Translate().ToString()))
            {
                if (sel != null && sel.recipe != null)
                {
                    // ★ 有正在跑的合成请求 ⇒ 开**实时树**（跟进度、可在树上操作）；否则开预览树
                    CraftJob live = comp.JobForRecipe(sel.recipe);
                    if (live != null)
                    {
                        Find.WindowStack.Add(new Window_AE2CraftTreeGraph(comp, live));
                    }
                    else
                    {
                        int want = (sel.mode == CraftPlan.ModeTarget) ? Mathf.Max(1, sel.targetCount)
                                 : ((sel.mode == CraftPlan.ModeCount) ? Mathf.Max(1, sel.remaining) : 1);
                        Find.WindowStack.Add(new Window_AE2CraftTreeGraph(comp, sel.recipe, sel.recipe.ProducedThingDef, want));
                    }
                }
            }
            if (AE2Draw.TextButton(new Rect(row.x, row.y, bw, HeaderH),
                    "DS_BA_ToggleState".Translate(onOff).ToString(), comp.Enabled))
            {
                comp.Enabled = !comp.Enabled;
            }
            if (AE2Draw.TextButton(new Rect(row.x + bw + 4f, row.y, bw, HeaderH),
                    "DS_BA_Overclock".Translate(comp.OverclockLabel()).ToString()))
            {
                comp.OverclockTier = (comp.OverclockTier + 1) % 4;
            }
            Rect addR = new Rect(row.xMax - 150f, row.y, 150f, HeaderH);
            if (AE2Draw.TextButton(addR, "DS_CA_AddRecipe".Translate().ToString(), true))
            {
                Find.WindowStack.Add(new Dialog_AE2AddRecipe(comp));
            }
            y += HeaderH + 2f;

            Rect info = new Rect(ri.x, y, ri.width, InfoH);
            AE2Draw.Sunken(info, AE2Draw.BarBg);
            string watts = "DS_BA_Watts".Translate(
                comp.CurrentWatts.ToString("#####0"),
                comp.Props.basePowerWatts.ToString("#####0"),
                comp.BenchWatts().ToString("#####0"),
                comp.OverclockPowerMult.ToString("0")).ToString();
            string stats = "DS_BA_Stats".Translate(comp.CompletedCount, comp.DroppedCount, comp.PlansForReading.Count).ToString();
            AE2Draw.Tiny(new Rect(info.x + 4f, info.y + 1f, info.width - 8f, 16f), watts + "　·　" + stats, AE2Draw.Hi);
            return y + InfoH + 3f;
        }

        // ---- 统计栏 = 筛选按钮（原版左栏那六项）----
        private float DrawFilterBar(Rect ri, float y, IList<CraftPlan> plans)
        {
            int[] count = new int[6];
            for (int i = 0; i < plans.Count; i++) count[Classify(plans[i])]++;
            count[F_All] = plans.Count;

            string[] labels = new string[6];
            labels[F_All] = "DS_CA_Orders".Translate().ToString();
            labels[F_Running] = "DS_CA_Working".Translate().ToString();
            labels[F_Waiting] = "DS_CA_Waiting".Translate().ToString();
            labels[F_Blocked] = "DS_AE2_Blocked".Translate().ToString();
            labels[F_Suspended] = "DS_BA_Suspended".Translate().ToString();
            labels[F_Done] = "DS_BA_PlanFinished".Translate().ToString();

            float x = ri.x;
            float w = Mathf.Max(78f, (ri.width - 5f * 4f) / 6f);
            for (int i = 0; i < 6; i++)
            {
                Rect b = new Rect(x, y, w, FilterH);
                if (AE2Draw.TextButton(b, labels[i] + " " + count[i], filter == i)) filter = i;
                if (Mouse.IsOver(b)) TooltipHandler.TipRegion(b, (string)(labels[i] + "：" + FilterHint(i)));   // B5
                x += w + 4f;
            }
            return y + FilterH + 4f;
        }

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
            AE2Draw.PanelBox(area);
            Rect li = area.ContractedBy(5f);
            AE2Draw.Tiny(new Rect(li.x + 2f, li.y, li.width - 14f, 18f),
                "DS_AE2_CoreItems".Translate().ToString() + "　·　" + CoreSummary(map), AE2Draw.TextDimCol);

            // ★ 用户要求：列表最上面加搜索栏（按名称过滤）
            Rect searchR = new Rect(li.x, li.y + 20f, li.width - 14f, 20f);   // A1：标题让位
            searchCore = AE2Draw.TextField(searchR, searchCore);

            Rect list = new Rect(li.x, searchR.yMax + 3f, li.width, li.yMax - searchR.yMax - 3f);

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
                AE2Draw.Row(row, hover, false);
                Rect ic = new Rect(row.x + 2f, row.y + 1f, 18f, 18f);
                AE2Draw.SlotBox(ic, false);
                Widgets.DefIcon(ic.ContractedBy(1f), items[idx].Key);
                AE2Draw.Tiny(new Rect(row.x + 23f, row.y + 3f, row.width - 62f, 14f),
                    items[idx].Key.LabelCap.ToString(), AE2Draw.TextCol);
                AE2Draw.Tiny(new Rect(row.xMax - 40f, row.y + 3f, 38f, 14f), "×" + items[idx].Value, AE2Draw.TextDimCol);
                if (hover)
                {
                    TooltipHandler.TipRegion(row, (string)(items[idx].Key.LabelCap + " ×" + items[idx].Value
                        + "\n左键点一下 ⇒ 弹窗填数量取出"));
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
                AE2Draw.Tiny(new Rect(list.x + 2f, list.y + 2f, list.width - 4f, 16f),
                    "DS_AE2_CoreEmpty".Translate().ToString(), AE2Draw.TextDimCol);
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
            AE2Draw.PanelBox(area);
            Rect li = area.ContractedBy(5f);

            List<CraftPlan> shown = new List<CraftPlan>();
            for (int i = 0; i < plans.Count; i++)
            {
                if (plans[i] == null) continue;
                if (filter == F_All || Classify(plans[i]) == filter) shown.Add(plans[i]);
            }

            Rect list = new Rect(li.x, li.y, li.width, li.height);
            if (shown.Count == 0)
            {
                AE2Draw.Tiny(new Rect(list.x + 3f, list.y + 3f, list.width - 6f, 16f),
                    (plans.Count == 0) ? "DS_CA_PickOrder".Translate().ToString() : "DS_CA_NoOrder".Translate().ToString(),
                    AE2Draw.TextDimCol);
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
                AE2Draw.Row(row, Mouse.IsOver(row), isSel);

                Rect ic = new Rect(row.x + 2f, row.y + 3f, 18f, 18f);
                AE2Draw.SlotBox(ic, false);
                Widgets.DefIcon(ic.ContractedBy(1f), p.recipe.ProducedThingDef);

                Color tc = isSel ? AE2Draw.Hi : AE2Draw.TextCol;
                CraftPlan __p2 = plans[i];                 bool __sub2 = (__p2 != null && __p2.recipe != null && comp.IsSubordinateStep(__p2.recipe));
                if (__sub2) AE2Draw.Tiny(new Rect(row.x + 22f, row.y + 4f, 16f, 17f), "└", AE2Draw.TextDimCol);
                AE2Draw.Small(new Rect(row.x + (__sub2 ? 40f : 23f), row.y + 2f, row.width - (__sub2 ? 95f : 78f), 21f),
                    p.recipe.ProducedThingDef.LabelCap.ToString(), tc);
                AE2Draw.Tiny(new Rect(row.xMax - 52f, row.y + 4f, 50f, 14f), CountLabel(p),
                    isSel ? AE2Draw.Hi : AE2Draw.TextDimCol);

                if (Mouse.IsOver(row)) TooltipHandler.TipRegion(row, (string)(p.recipe.LabelCap + "\n" + StatusText(p)));
                // D11：行内进度条（正在做的那一件）
                CraftLine __fl = (p.lines.Count > 0) ? p.lines[0] : null;
                float __pct = (__fl != null && __fl.HasWork) ? __fl.Progress01 : (p.Done ? 1f : 0f);
                AE2Draw.Bar(new Rect(row.xMax - 46f, row.y + 17f, 44f, 5f), __pct, isSel);

                // D10：右键菜单（暂停 / +10 / 改为无限 / 删除）
                if (Mouse.IsOver(row) && Event.current != null && Event.current.type == EventType.MouseDown && Event.current.button == 1)
                {
                    Event.current.Use();
                    List<FloatMenuOption> mo = new List<FloatMenuOption>();
                    CraftPlan cap = p;
                    mo.Add(new FloatMenuOption(cap.suspended ? "DS_BA_Resume".Translate().ToString() : "DS_BA_Suspend".Translate().ToString(), delegate { cap.suspended = !cap.suspended; }));
                    mo.Add(new FloatMenuOption("+10", delegate { cap.AddCount(10); }));
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
            AE2Draw.PanelBox(area);
            Rect ri = area.ContractedBy(5f);
            float y = ri.y;

            if (sel == null || sel.recipe == null)
            {
                AE2Draw.Tiny(new Rect(ri.x + 3f, ri.y + 3f, ri.width - 6f, 18f),
                    "DS_CA_PickOrder".Translate().ToString(), AE2Draw.TextDimCol);
                return;
            }

            CraftPlan p = sel;
            comp.RefreshTargetStateIfStale(p, map);
            CraftLine first = (p.lines.Count > 0) ? p.lines[0] : null;

            Rect head = new Rect(ri.x, y, ri.width, 26f);
            AE2Draw.Sunken(head, AE2Draw.BarBg);
            Rect hi = new Rect(head.x + 3f, head.y + 4f, 18f, 18f);
            AE2Draw.SlotBox(hi, false);
            Widgets.DefIcon(hi.ContractedBy(1f), p.recipe.ProducedThingDef);
            AE2Draw.Small(new Rect(head.x + 25f, head.y + 3f, head.width - 80f, 20f),
                p.recipe.ProducedThingDef.LabelCap + " " + CountLabel(p), AE2Draw.Hi);
            AE2Draw.Tiny(new Rect(head.xMax - 56f, head.y + 5f, 52f, 16f), ModeShort(p), AE2Draw.Hi);
            y += 30f;

            AE2Draw.Tiny(new Rect(ri.x + 2f, y, ri.width - 4f, 15f),
                "DS_CA_Sub".Translate(p.recipe.ProducedThingDef.LabelCap, SkillText(p.recipe)).ToString(), AE2Draw.TextCol);
            y += 19f;   // C6：行距放宽
            // A2 修 bug：原来 "在产" 与 "在产 x/y 台" 重复，只留后者
            AE2Draw.Tiny(new Rect(ri.x + 2f, y, ri.width - 4f, 19f),
                "DS_BA_Lines".Translate(BusyLines(p), comp.UsableBenchCountFor(p.recipe)).ToString(), AE2Draw.TextCol);
            y += 19f;   // C6：行距放宽

            int cls = Classify(p);
            string st = StatusText(p);
            if (p.lines.Count == 0 && p.mode == CraftPlan.ModeTarget && p.countedCount < p.targetCount) cls = F_Blocked;   // B4：缺台子标红
            Color stCol = (cls == F_Done) ? AE2Draw.Accent
                : ((cls == F_Suspended) ? AE2Draw.TextDimCol
                : ((cls == F_Blocked) ? AE2Draw.Bad : ((cls == F_Running) ? AE2Draw.Accent : AE2Draw.TextCol)));
            AE2Draw.Tiny(new Rect(ri.x + 2f, y, 40f, 15f), "DS_CA_Status".Translate().ToString(), AE2Draw.TextDimCol);
            AE2Draw.Tiny(new Rect(ri.x + 44f, y, ri.width - 48f, 15f), st, stCol);
            y += 18f;

            float pct = (first != null && first.HasWork) ? first.Progress01 : (p.Done ? 1f : 0f);
            AE2Draw.Tiny(new Rect(ri.x + 2f, y, 120f, 15f), "DS_CA_Progress".Translate().ToString(), AE2Draw.TextDimCol);
            string progTxt = "DS_CA_Percent".Translate((int)(pct * 100f)).ToString();
            if (first != null && first.HasWork)
                progTxt += "  ·  " + "DS_CA_RemainTime".Translate(RemainSeconds(first).ToString("0")).ToString();
            AE2Draw.Tiny(new Rect(ri.xMax - 170f, y, 168f, 15f), progTxt, AE2Draw.TextCol);
            y += 19f;   // C6：行距放宽
            AE2Draw.Bar(new Rect(ri.x + 2f, y, ri.width - 4f, 10f), pct, false);
            y += 14f;

            AE2Draw.Tiny(new Rect(ri.x + 2f, y, ri.width - 4f, 15f), "DS_CA_Needs".Translate().ToString(), AE2Draw.TextDimCol);
            y += 19f;   // C6：行距放宽
            y = DrawMaterials(p, first, ri, y, map);

            // ---- 按钮区（原版全套 + 改材料）----
            float bh = 24f;
            y = Mathf.Max(y + 4f, ri.yMax - 3f * (bh + 4f) - 4f);   // ★ 三行按钮：给「风格」腾位置
            float x = ri.x;

            if (AE2Draw.TextButton(new Rect(x, y, 116f, bh), "DS_CA_ModeBtn".Translate(ModeLabel(p)).ToString()))
            {
                comp.CycleMode(p);
                // ★ 切到"维持"时顺手打开「达标即暂停」：否则维持模式会一直做下去（用户实测踩过）
                // ★ 注意：维持模式（ModeTarget）的语义是"保持库存 ≥N"，产物被下游吃掉会**自动补做** ⇒
                //   想"只做 N 件就停"请用**次数**模式（本面板顶栏按模式下一次会切到它）。
                if (p.mode == CraftPlan.ModeTarget && !p.pauseWhenSatisfied) p.pauseWhenSatisfied = true;
            }
            x += 120f;
            if (AE2Draw.TextButton(new Rect(x, y, 42f, bh), "−10")) p.AddCount(-10);
            x += 46f;
            if (AE2Draw.TextButton(new Rect(x, y, 36f, bh), "−1")) p.AddCount(-1);
            x += 40f;

            Rect countR = new Rect(x, y, 58f, bh);
            if (countBufPlan != p)
            {
                countBufPlan = p;
                countBuf = (p.mode == CraftPlan.ModeTarget) ? p.targetCount.ToString() : Mathf.Max(0, p.remaining).ToString();
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
                }
            }
            x += 62f;
            if (AE2Draw.TextButton(new Rect(x, y, 36f, bh), "+1")) p.AddCount(1);
            x += 40f;
            if (AE2Draw.TextButton(new Rect(x, y, 42f, bh), "+10")) p.AddCount(10);
            x += 46f;
            string susLabel = p.suspended ? "DS_BA_Resume".Translate().ToString() : "DS_BA_Suspend".Translate().ToString();
            if (AE2Draw.TextButton(new Rect(x, y, 66f, bh), susLabel)) p.suspended = !p.suspended;
            y += bh + 4f;

            x = ri.x;
            if (AE2Draw.TextButton(new Rect(x, y, 56f, bh), "DS_CA_Up".Translate().ToString())) comp.MovePlan(p, -1);
            x += 60f;
            if (AE2Draw.TextButton(new Rect(x, y, 56f, bh), "DS_CA_Down".Translate().ToString())) comp.MovePlan(p, 1);
            x += 60f;
            if (AE2Draw.TextButton(new Rect(x, y, 88f, bh), "DS_CA_SetForever".Translate().ToString())) p.SetForever();
            x += 92f;
            if (AE2Draw.TextButton(new Rect(x, y, 74f, bh), "DS_CA_PauseWhenSatisfied".Translate().ToString(), p.pauseWhenSatisfied))
            {
                p.pauseWhenSatisfied = !p.pauseWhenSatisfied;
            }
            x += 78f;

            // ---- 第三行：改制作材料（未完成品兼容那条链认 allowedStuff）· 改风格（落到 Bill.style ⇒ 产物带风格）· 删除 ----
            y += bh + 4f;
            x = ri.x;
            // ★ 用户点名要的「改制作材料」按钮（未完成品兼容那条链认 CraftPlan.allowedStuff）
            if (AE2Draw.TextButton(new Rect(x, y, 120f, bh),
                    "DS_AE2_Stuff".Translate(StuffName(p)).ToString(), p.allowedStuff != null))
            {
                OpenStuffMenu(p);
            }
            x += 124f;
            if (AE2Draw.TextButton(new Rect(x, y, 110f, bh),
                    "DS_AE2_Style".Translate(StyleName(p)).ToString(), p.styleDef != null))
            {
                OpenStyleMenu(p);
            }
            x += 114f;
            if (AE2Draw.TextButton(new Rect(x, y, 74f, bh), "DS_CA_Delete".Translate().ToString()))
            {
                comp.RemovePlan(p);
                sel = null;
            }
            x += 124f;
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
            return r.workAmount.ToString("0");
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
                    AE2Draw.Tiny(new Rect(ri.x + 4f, y, ri.width - 8f, 15f),
                        "DS_CA_Have".Translate(need + "×" + t.def.LabelCap, have).ToString(),
                        (have < need) ? AE2Draw.Bad : AE2Draw.TextCol);
            y += 19f;   // C6：行距放宽
                }
                return y;
            }

            if (p.recipe.ingredients == null || p.recipe.ingredients.Count == 0)
            {
                AE2Draw.Tiny(new Rect(ri.x + 4f, y, ri.width - 8f, 15f),
                    "DS_CA_NoMaterials".Translate().ToString(), AE2Draw.TextDimCol);
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
                AE2Draw.Tiny(new Rect(ri.x + 4f, y, ri.width - 8f, 15f),
                    "DS_AE2_NeedWithCore".Translate(names, __need, have).ToString(), (have >= __need) ? AE2Draw.TextCol : AE2Draw.Bad);
                    y += 19f;   // ★ 每种材料往下走一行（之前这行被编辑吃掉 ⇒ 二层甲/二层乙叠在一起）
            }
            return y;
        }

        /// <summary>从核心取出若干件放到代理旁（放不下塞回核心，不丢东西）。</summary>
        /// <summary>C9：左栏标题摘要（多少种 / 多少件）。</summary>
        private static string CoreSummary(Map map)
        {
            List<KeyValuePair<ThingDef, int>> it = CoreItems(map);

            int total = 0;
            for (int k = 0; k < it.Count; k++) total += it[k].Value;
            return it.Count + " 种 · " + total + " 件";
        }

        /// <summary>D12：本图第一台数字存储核心（供"存储核心"快捷跳转）。</summary>
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
            if (opts.Count == 0) opts.Add(new FloatMenuOption("本图没有制作代理", null));
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
                case F_All: return "全部订单";
                case F_Running: return "已经有工作台在做的订单";
                case F_Waiting: return "排上了但还没有空闲工作台（或在等上一件做完）";
                case F_Blocked: return "被挡住：材料不够 / 范围内没有能做工的工作台";
                case F_Suspended: return "你手动挂起的订单";
                case F_Done: return "已完成或已达标（维持模式下产物被用掉会自动恢复）";
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
