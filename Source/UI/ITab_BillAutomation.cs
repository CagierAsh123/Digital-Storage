// =====================================================================================
//  【本地新增文件／整块替换】ITab_BillAutomation —— 制作代理的检查页签（AE2 材质版）
// -------------------------------------------------------------------------------------
//  这是**整块替换**：源文件在 ds-src 里是环世界默认皮肤（Widgets.ButtonText/Label），
//  这里换成 AE2 材质（AE2Draw：凸起按钮 / 凹槽 / 行 / 进度条 / AE2 文字色），
//  功能与文案 key 一个没少：
//    · 打开 AE2 面板（主入口）＋ 打开旧面板（次级，目标④之后可移除）
//    · 总开关 / 超频
//    · 耗电与统计
//    · 在产摘要（前几行，带**真实进度条**与 AE2 侧面拖动条）
//
//  ⚠️ 约束：本窗口**不用** BeginScrollView（ITab 里那对调用最容易把 GUI 栈弄失衡）；
//     行数按高度算 + AE2Draw.DragBar 自己处理拖动，见 AE2Draw 文件头。
// =====================================================================================
using System;   // ★ 第 12 轮：界面绘制护栏要用 Exception
using System.Collections.Generic;
using DigitalStorage.AI;
using DigitalStorage.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    public class ITab_BillAutomation : ITab
    {
        private static readonly Vector2 WinSize = new Vector2(430f, 330f);
        private const float RowH = 22f;
        private float scroll;

        private CompBillAutomation Comp
        {
            get { return (SelThing == null) ? null : SelThing.TryGetComp<CompBillAutomation>(); }
        }

        public ITab_BillAutomation()
        {
            this.size = WinSize;
            this.labelKey = "DS_TabBillAutomation";
        }

        public override bool IsVisible
        {
            get { return Comp != null; }
        }

        protected override void FillTab()
        {
            try
            {
            CompBillAutomation comp = Comp;
            if (comp == null) return;

            Rect r = new Rect(0f, 0f, WinSize.x, WinSize.y).ContractedBy(4f);
            AE2Draw.HandlePauseHotkey();   // ★ 用户要求：界面开着时空格也能暂停/继续
            AE2Draw.PanelBox(r);
            Rect ri = r.ContractedBy(6f);
            float y = ri.y;

            // ① 主入口：AE2 面板
            if (AE2Draw.TextButton(new Rect(ri.x, y, ri.width, 28f), "DS_AE2_OpenPanel".Translate().ToString(), true))
            {
                Find.WindowStack.Add(new Window_AE2CraftPanel(comp));
            }
            y += 32f;

            // ② 开关 / 超频 / 旧面板
            string state = comp.Enabled ? "DS_BA_On".Translate().ToString() : "DS_BA_Off".Translate().ToString();
            float third = (ri.width - 4f) / 2f;   // ★ 目标④：两颗按钮（AE2 面板 / 开关）
            if (AE2Draw.TextButton(new Rect(ri.x, y, third, 24f), "DS_BA_ToggleState".Translate(state).ToString()))
            {
                comp.Enabled = !comp.Enabled;
            }
            if (AE2Draw.TextButton(new Rect(ri.x + third + 4f, y, third, 24f),
                    "DS_BA_Overclock".Translate(comp.OverclockLabel()).ToString()))
            {
                comp.OverclockTier = (comp.OverclockTier + 1) % 4;
            }
            // ★ 目标④：旧面板已退役（无任何入口能构造它）⇒ 页签不再提供"旧面板"按钮
            y += 28f;

            // ③ 耗电 / 统计（AE2 凹槽 + 亮字）
            Rect info = new Rect(ri.x, y, ri.width, 40f);
            AE2Draw.Sunken(info, AE2Draw.BarBg);
            AE2Draw.Tiny(new Rect(info.x + 4f, info.y + 3f, info.width - 8f, 16f),
                "DS_BA_Watts".Translate(
                    comp.CurrentWatts.ToString("#####0"),
                    comp.Props.basePowerWatts.ToString("#####0"),
                    comp.BenchWatts().ToString("#####0"),
                    comp.OverclockPowerMult.ToString("0")).ToString(), AE2Draw.Hi);
            AE2Draw.Tiny(new Rect(info.x + 4f, info.y + 21f, info.width - 8f, 16f),
                "DS_BA_Stats".Translate(comp.CompletedCount, comp.DroppedCount, comp.PlansForReading.Count).ToString(),
                AE2Draw.Hi);
            y += 44f;

            // ④ 在产摘要（AE2 行 + 真实进度条 + 侧面拖动条）
            IList<CraftPlan> plans = comp.PlansForReading;
            Rect list = new Rect(ri.x, y, ri.width, ri.yMax - y);
            if (plans.Count == 0)
            {
                AE2Draw.Tiny(new Rect(list.x + 4f, list.y + 2f, list.width - 8f, 18f), "DS_BA_Empty".Translate().ToString(), AE2Draw.TextDimCol);
                return;
            }

            int vis = Mathf.Max(1, Mathf.FloorToInt(list.height / (RowH + 1f)));
            int maxStart = Mathf.Max(0, plans.Count - vis);
            int start = Mathf.RoundToInt(scroll * maxStart);
            int drawn = Mathf.Min(vis, Mathf.Max(0, plans.Count - start));

            for (int i = 0; i < drawn; i++)
            {
                CraftPlan plan = plans[start + i];
                if (plan == null) continue;
                Rect row = new Rect(list.x, list.y + i * (RowH + 1f), list.width - 14f, RowH);
                AE2Draw.Row(row, Mouse.IsOver(row), i == 0);

                CraftLine first = (plan.lines.Count > 0) ? plan.lines[0] : null;
                string label = (plan.recipe == null) ? "?" : plan.recipe.LabelCap.ToString();
                float pct;
                string tail;
                if (plan.Done) { pct = 1f; tail = "DS_BA_PlanFinished".Translate().ToString(); }
                else if (plan.suspended) { pct = 0f; tail = "DS_BA_Suspended".Translate().ToString(); }
                else if (plan.mode == CraftPlan.ModeTarget && plan.paused) { pct = 0f; tail = plan.countedCount + "/" + plan.targetCount; }
                else if (first != null && first.HasWork) { pct = first.Progress01; tail = (pct * 100f).ToString("0") + "%"; }
                else { pct = 0f; tail = "DS_CA_Waiting".Translate().ToString(); }

                AE2Draw.Small(new Rect(row.x + 4f, row.y + 2f, row.width - 84f, 18f), label, i == 0 ? AE2Draw.Hi : AE2Draw.TextCol);
                Rect bar = new Rect(row.xMax - 74f, row.y + 7f, 46f, 8f);
                AE2Draw.Bar(bar, pct, i == 0);
                AE2Draw.Tiny(new Rect(row.xMax - 26f, row.y + 3f, 24f, 16f), tail, i == 0 ? AE2Draw.Hi : AE2Draw.TextDimCol);
            }

            AE2Draw.DragBar(new Rect(list.xMax - 12f, list.y, 12f, list.height), ref scroll,
                (plans.Count <= 0) ? 1f : Mathf.Clamp01((float)vis / plans.Count));
            }
            catch (Exception __uiEx) { Log.ErrorOnce("[DigitalStorage] AE2 界面绘制异常（只记一次，界面不会卡死）：" + __uiEx, 771004); }
        }
    }
}
