// =====================================================================================
//  【本地新增文件】Dialog_AE2AddRecipe —— AE2 风格「添加配方」窗（目标②的核心）
// -------------------------------------------------------------------------------------
//  为什么要有它：旧面板退役的前提是**新界面自己能下单**。这个窗就是新界面的下单入口：
//    · 搜索框（按产物/配方名过滤代理范围内的可用配方）
//    · 可拖动滚动的配方列表（AE2 侧面拖动条）
//    · 选中后底部有：产物 / 数量（−10 −1 +1 +10 / 维持）/ 材料（AE2 的"限定材料"）/ 提交
//  提交走的就是内核那条路：SubmitJob(recipe, null, want, withIntermediates: true) —— 一个请求 = 一棵依赖树；
//  材料选择落到根订单的 allowedStuff（A 组加进 CraftPlan 的字段，BillProbe 取料时认它）。
// =====================================================================================
using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using DigitalStorage.AI;
using DigitalStorage.Components;

namespace DigitalStorage.UI
{
    public class Dialog_AE2AddRecipe : Window
    {
        private readonly CompBillAutomation comp;
        private string search = "";
        private string catFilter = null;   // ★ 用户要求：单条"分类"筛选（null = 全部）
        private float scroll;
        private RecipeDef sel;
        private int count = 1;
        private int rootMode;   // ★ 0=维持（默认，保持库存）1=次数（做满就停）2=无限
        private ThingDef stuff;
        private ThingStyleDef style;   // ★ 风格（落到 CraftPlan.styleDef ⇒ Bill.style）

        private const float RowH = 26f;
        private const float BottomH = 158f;   // ★ 四行要的高度（底排按钮 24→32 ⇒ +16）

        public Dialog_AE2AddRecipe(CompBillAutomation comp)
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

        public override Vector2 InitialSize { get { return new Vector2(560f, 520f); } }   // ★ 460→560：材料/风格按钮要放长材料名

        public override void DoWindowContents(Rect inRect)
        {
            try
            {
            if (comp == null || comp.parent == null) { Close(); return; }

            // ★ 目标③：外框统一走 AE2Draw.WindowFrame（面板 + 标题带 + 面板内右上角的 ×）
            bool closeClicked;
            AE2Draw.HandlePauseHotkey();   // ★ 用户要求：界面开着时空格也能暂停/继续
            Rect ri = AE2Draw.WindowFrame(inRect.ContractedBy(2f), "DS_AE2_AddTitle".Translate().ToString(), out closeClicked);
            if (closeClicked) { Close(); return; }

            // ---- 搜索框（AE2 风格输入框）----
            Rect sRect = new Rect(ri.x, ri.y, ri.width, 22f);
            // ★ 用户要求：搜索框**前面**加一个"分类"（单条，点开选择，不是多个页签）
            Rect catR = new Rect(sRect.x, sRect.y, 84f, sRect.height);
            string catLabel = (catFilter == null) ? "DS_AE2_CatAll".Translate().ToString() : catFilter;
            if (AE2Draw.TextButton(catR, catLabel)) OpenCategoryMenu();
            Rect sR2 = new Rect(sRect.x + 88f, sRect.y, Mathf.Max(60f, sRect.width - 88f), sRect.height);
            string newSearch = AE2Draw.TextField(sR2, search ?? "");
            if (newSearch != search) { search = newSearch; scroll = 0f; }

            // ---- 底部：选中信息 + 数量 + 材料 + 提交 ----
            Rect bottom = new Rect(ri.x, ri.yMax - BottomH, ri.width, BottomH);
            AE2Draw.Sunken(bottom, AE2Draw.Slot);
            DrawBottom(bottom.ContractedBy(4f));

            // ---- 配方列表（可拖动滚动）----
            Rect list = new Rect(ri.x, sRect.yMax + 4f, ri.width, bottom.y - sRect.yMax - 8f);
            DrawList(list);
            }
            catch (Exception __uiEx) { Log.ErrorOnce("[DigitalStorage] AE2 界面绘制异常（只记一次，界面不会卡死）：" + __uiEx, 771002); }
        }

        /// <summary>配方列表：来源=可解锁配方 ∪ 挂在制作代理上的配方；过滤=分类+搜索；拖动条+滚轮。</summary>
        private void DrawList(Rect area)
        {
            List<RecipeDef> all = new List<RecipeDef>();
            IList<RecipeDef> unlocked = comp.UnlockedRecipes;
            if (unlocked != null)
            {
                for (int i = 0; i < unlocked.Count; i++)
                {
                    RecipeDef r = unlocked[i];
                    if (r != null && !all.Contains(r)) all.Add(r);
                }
            }
            // ★ 关键：并进"所有挂在制作代理上的配方"（自定义配方不在 UnlockedRecipes 里）
            foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefs)
            {
                if (r == null || r.ProducedThingDef == null || r.recipeUsers == null) continue;
                bool onProxy = false;
                for (int u = 0; u < r.recipeUsers.Count; u++)
                {
                    ThingDef uu = r.recipeUsers[u];
                    if (uu != null && uu.defName != null && uu.defName.StartsWith("DigitalStorage_WorkerCraft")) { onProxy = true; break; }
                }
                if (onProxy && !all.Contains(r)) all.Add(r);
            }

            // 过滤：分类 + 搜索（叠加）
            List<RecipeDef> shown = new List<RecipeDef>();
            for (int i = 0; i < all.Count; i++)
            {
                RecipeDef rd = all[i];
                if (rd == null || rd.ProducedThingDef == null) continue;
                if (catFilter != null && CatOf(rd.ProducedThingDef) != catFilter) continue;
                if (!string.IsNullOrEmpty(search))
                {
                    string lab = rd.LabelCap.ToString();
                    string pl = rd.ProducedThingDef.LabelCap.ToString();
                    if (lab.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0
                        && pl.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                shown.Add(rd);
            }

            if (shown.Count == 0)
            {
                AE2Draw.TextIn(new Rect(area.x + 4f, area.y + 4f, area.width - 8f, 17f), (string.IsNullOrEmpty(search) ? "DS_AE2_NoRecipeInCat".Translate().ToString() : "DS_AE2_NoRecipeMatch".Translate(search).ToString()), 1f, AE2Draw.TextDimCol, TextAnchor.MiddleLeft);
                return;
            }

            const float rowH = 26f;
            int vis = Mathf.Max(1, Mathf.FloorToInt((area.height - 2f) / (rowH + 1f)));
            int maxStart = Mathf.Max(0, shown.Count - vis);
            int start = Mathf.RoundToInt(scroll * maxStart);
            int drawn = Mathf.Min(vis, Mathf.Max(0, shown.Count - start));
            for (int i = 0; i < drawn; i++)
            {
                RecipeDef rd = shown[start + i];
                Rect row = new Rect(area.x, area.y + i * (rowH + 1f), area.width - 14f, rowH);
                bool isSel = (rd == sel);
                AE2Draw.Row(row, Mouse.IsOver(row), isSel);

                Rect ic = new Rect(row.x + 3f, row.y + 3f, 20f, 20f);
                AE2Draw.SlotBox(ic, false);
                Widgets.DefIcon(ic.ContractedBy(1f), rd.ProducedThingDef);

                // ★ 未选中用深色（之前因为 Small() 被改坏，颜色没生效 ⇒ 看起来是白字）
                Color tc = isSel ? AE2Draw.Hi : AE2Draw.TextCol;
                AE2Draw.TextIn(new Rect(row.x + 28f, row.y + 2f, row.width - 32f, 21f), rd.ProducedThingDef.LabelCap.ToString() + "　" + rd.LabelCap.ToString(), 1f, tc, TextAnchor.MiddleLeft);

                if (Mouse.IsOver(row)) TooltipHandler.TipRegion(row, (string)(rd.LabelCap + "\n" + rd.description));
                if (Widgets.ButtonInvisible(row)) sel = rd;
            }

            if (shown.Count > vis)
            {
                AE2Draw.DragBar(new Rect(area.xMax - 12f, area.y, 12f, area.height), ref scroll,
                    Mathf.Clamp01((float)vis / shown.Count));
            }
            AE2Draw.WheelScroll(area, ref scroll, maxStart);   // ★ 鼠标在列表里滚滚轮也能滚
        }

        /// <summary>★ 物品的"顶层分类"名（与存储页签分组同一口径：原料/食物/制成品/药品…）。</summary>
        private static string CatOf(ThingDef d)
        {
            if (d == null || d.FirstThingCategory == null) return null;
            ThingCategoryDef c = d.FirstThingCategory;
            // ★ 停在"根目录"的**下一层**（原料/食物/制成品/药品…）；一路走到最顶只会得到「根目录」一项
            while (c.parent != null && c.parent.parent != null) c = c.parent;
            return c.LabelCap.ToString();
        }

        /// <summary>★ 分类选择菜单（单条按钮点开选一个）：从"全部挂在制作代理上的配方"统计分类。</summary>
        private void OpenCategoryMenu()
        {
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            opts.Add(new FloatMenuOption("DS_AE2_CatAll".Translate().ToString(), delegate { catFilter = null; }));
            List<string> cats = new List<string>();
            foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefs)
            {
                if (r == null || r.ProducedThingDef == null || r.recipeUsers == null) continue;
                bool onProxy = false;
                for (int u = 0; u < r.recipeUsers.Count; u++)
                {
                    ThingDef uu = r.recipeUsers[u];
                    if (uu != null && uu.defName != null && uu.defName.StartsWith("DigitalStorage_WorkerCraft")) { onProxy = true; break; }
                }
                if (!onProxy) continue;
                string c = CatOf(r.ProducedThingDef);
                if (c != null && !cats.Contains(c)) cats.Add(c);
            }
            IList<RecipeDef> unlocked = comp.UnlockedRecipes;
            if (unlocked != null)
            {
                for (int i = 0; i < unlocked.Count; i++)
                {
                    RecipeDef r = unlocked[i];
                    string c = (r == null) ? null : CatOf(r.ProducedThingDef);
                    if (c != null && !cats.Contains(c)) cats.Add(c);
                }
            }
            cats.Sort();
            for (int i = 0; i < cats.Count; i++)
            {
                string cap = cats[i];
                opts.Add(new FloatMenuOption(cap, delegate { catFilter = cap; }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private void DrawBottom(Rect b)
        {
            if (sel == null || sel.ProducedThingDef == null)
            {
                AE2Draw.TextIn(new Rect(b.x + 2f, b.y + 4f, b.width - 4f, 17f), "DS_AE2_PickRecipeFirst".Translate().ToString(), 1f, AE2Draw.TextDimCol, TextAnchor.MiddleLeft);
                return;
            }

            string stuffLabel = "DS_AE2_Stuff".Translate((stuff == null) ? "DS_AE2_StuffAny".Translate().ToString() : stuff.LabelCap.ToString()).ToString();
            string styleLabel = "DS_AE2_Style".Translate((style == null) ? "DS_AE2_StyleAny".Translate().ToString() : style.LabelCap.ToString()).ToString();

            // ① 产物
            Rect ic = new Rect(b.x, b.y + 2f, 20f, 20f);
            AE2Draw.SlotBox(ic, false);
            Widgets.DefIcon(ic.ContractedBy(1f), sel.ProducedThingDef);
            AE2Draw.TextIn(new Rect(b.x + 26f, b.y + 2f, b.width - 30f, 21f), sel.ProducedThingDef.LabelCap.ToString(), 1f, AE2Draw.Hi, TextAnchor.MiddleLeft);

            // ② 数量步进（−10 −1 [输入] +1 +10）
            float rowY = b.y + 28f;
            if (Btn(new Rect(b.x, rowY, 34f, 24f), "−10")) count = Mathf.Max(1, count - 10);
            if (Btn(new Rect(b.x + 36f, rowY, 30f, 24f), "−1")) count = Mathf.Max(1, count - 1);
            Rect cf = new Rect(b.x + 68f, rowY, 76f, 24f);
            AE2Draw.Sunken(cf, AE2Draw.Slot);
            string typedCount = Widgets.TextField(cf.ContractedBy(3f), (rootMode == 2) ? "∞" : count.ToString());
            int parsed;
            if (int.TryParse(typedCount, out parsed) && parsed > 0) { count = Mathf.Min(99999, parsed); if (rootMode == 2) rootMode = 0; }
            if (Btn(new Rect(b.x + 146f, rowY, 30f, 24f), "+1")) count = Mathf.Min(99999, count + 1);
            if (Btn(new Rect(b.x + 178f, rowY, 34f, 24f), "+10")) count = Mathf.Min(99999, count + 10);

            // ②-b ★ 模式：改成一排方按钮（原版 AE2 是并列可选，不是循环切换）
            //   旧版是单个循环按钮（维持→次数→无限），容易点错、也看不出当前模式。
            float modeY = rowY + 26f;
            string[] modeLabels2 = new string[]
            {
                "DS_CA_ModeKeepShort".Translate().ToString(),
                "DS_CA_ModeCountShort".Translate().ToString(),
                "DS_CA_ModeForeverShort".Translate().ToString(),
            };
            float modeW = Mathf.Min(b.width, 3f * 58f + 2f * 4f);
            int hitMode2 = AE2Draw.Segmented(new Rect(b.x, modeY, modeW, 24f), modeLabels2, rootMode, 4f);
            if (hitMode2 >= 0) rootMode = hitMode2;   // 0=维持 1=次数 2=无限

            // ③ 材料 / 风格 / 提交
            float cy = modeY + 28f;
            // ★ 材料 / 风格按钮**按文字自适应**（长材料名会被截断：如"材料：沃芬族深色吊带…"）
            // ★ 高度必须 ≥21px，否则 TextIn 会退回 Tiny 字体 ⇒ 与上面的"维持/次数/无限"（32px/Small）不一致
            const float BTN_H = 32f;
            const float SUBMIT_W = 150f, CGAP = 6f;
            float pairW = Mathf.Max(60f, (b.width - SUBMIT_W - CGAP * 2f) * 0.5f);
            if (Btn(new Rect(b.x, cy, pairW, BTN_H), stuffLabel)) OpenStuffMenu();
            if (Btn(new Rect(b.x + pairW + CGAP, cy, pairW, BTN_H), styleLabel)) OpenStyleMenu();
            Rect submit = new Rect(b.xMax - SUBMIT_W, cy, SUBMIT_W, BTN_H);
            bool canSubmit = comp.JobForRecipe(sel) == null;
            AE2Draw.AE2Button(submit, false, canSubmit && Mouse.IsOver(submit));   // ★ 主操作按钮：常态 + 悬停（原来走 FlatButton ⇒ 永远无 hover）
            AE2Draw.TextIn(new Rect(submit.x, submit.y, submit.width, submit.height), canSubmit ? "DS_AE2_Submit".Translate().ToString() : "DS_AE2_AlreadyInJob".Translate().ToString(), 1f, canSubmit ? AE2Draw.TextCol : AE2Draw.TextDimCol, TextAnchor.MiddleCenter);
            if (canSubmit && Widgets.ButtonInvisible(submit)) Submit();
        }

        private bool Btn(Rect r, string label)
        {
            bool hover = Mouse.IsOver(r);
            AE2Draw.AE2Button(r, false, hover);   // ★ 常态 + 悬停（原来走 FlatButton ⇒ 丢失 hover）
            AE2Draw.TextIn(new Rect(r.x, r.y, r.width, r.height), label, 1f, AE2Draw.TextCol, TextAnchor.MiddleCenter);   // ★ 居中且用满高度（≥21px ⇒ Small 字体）
            return Widgets.ButtonInvisible(r);
        }

        /// <summary>材料菜单：不限 + 这个配方能接受的每种材料（A 组的 BillProbe.UsableStuffs）。</summary>
        private void OpenStuffMenu()
        {
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            opts.Add(new FloatMenuOption("DS_AE2_StuffNone".Translate().ToString(), delegate { stuff = null; }));
            List<ThingDef> list = BillProbe.UsableStuffs(sel);
            if (list != null)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    ThingDef d = list[i];
                    if (d == null) continue;
                    opts.Add(new FloatMenuOption(d.LabelCap.ToString(), delegate { stuff = d; }));
                }
            }
            if (opts.Count == 1) opts.Add(new FloatMenuOption("DS_AE2_NoStuff".Translate().ToString(), null));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        /// <summary>改制作风格（默认 + 各文化风格；落到 CraftPlan.styleDef）。</summary>
        private void OpenStyleMenu()
        {
            List<FloatMenuOption> opts = new List<FloatMenuOption>();
            opts.Add(new FloatMenuOption("DS_AE2_StyleAny".Translate().ToString(), delegate { style = null; }));
            List<ThingStyleDef> all = DefDatabase<ThingStyleDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                ThingStyleDef sd = all[i];
                if (sd == null) continue;
                ThingStyleDef cap = sd;
                opts.Add(new FloatMenuOption(sd.LabelCap.ToString(), delegate { style = cap; }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private void Submit()
        {
            int want = (rootMode == 2) ? 1 : Mathf.Max(1, count);
            CraftJob job = comp.SubmitJob(sel, null, want, true);
            if (job == null) return;   // 被容量闸门拒绝时 SubmitJob 已经给过提示

            CraftPlan root = CraftTree.FindPlan(comp, sel);
            if (root != null)
            {
                if (stuff != null) root.allowedStuff = stuff;
                if (style != null) root.styleDef = style;
                // ★ 修"只要一个却做了五个"：数量 N 必须用**次数模式**（做满 N 件就停），
                //   而不是"维持 N"——维持模式在没开「达标即暂停」时会一直做下去（原版语义）。
                if (rootMode == 2)   // 无限
                {
                    root.SetForever();
                }
                else if (rootMode == 1)   // 次数：做满就停
                {
                    root.mode = CraftPlan.ModeCount;
                    root.remaining = Mathf.Max(1, want);
                    root.suspended = false;
                }
                else   // 维持（默认）：保持库里有 N 件，被用掉自动补做
                {
                    root.SetTarget(Mathf.Max(1, want));
                    root.pauseWhenSatisfied = true;
                }

            }
            Messages.Message("DS_JOB_Submitted".Translate(sel.LabelCap, job.wanted, job.steps.Count),
                MessageTypeDefOf.NeutralEvent, historical: false);
            Close();
        }
    }
}
