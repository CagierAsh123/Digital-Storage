using System;
using System.Collections.Generic;
using System.Text;
using DigitalStorage.AI;
using DigitalStorage.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// <b>"添加配方"窗口</b>（界面稿里的弹窗结构）：顶部<b>分类页签</b> + 搜索 + 列表，
    /// 点一行就加进该制作代理的订单列表。
    ///
    /// <para>候选来自 <see cref="CraftUnlocks"/>：13×13 范围内工作台能做、且研究已解锁的配方。
    /// 资质不够 / 已添加的**仍然显示**（灰色 + 标注），免得玩家以为"没有这个配方"。
    /// 分类用 <see cref="CraftCategories"/> 按产物 def 的真实数据判定（武器 / 装备与衣物 / 制成品 /
    /// 食物 / 药品与耗材 / 建筑与家具 / 其他）。</para>
    /// </summary>
    public class Dialog_DS_AddCraft : Window
    {
private readonly CompBillAutomation comp;
        private readonly List<RecipeDef> shown = new List<RecipeDef>();
        private string search = "";
        private int catIndex;
        private Vector2 scroll;

        public Dialog_DS_AddCraft(CompBillAutomation comp)
        {
            this.comp = comp;
            this.doCloseX = true;
            this.closeOnClickedOutside = true;
            this.absorbInputAroundWindow = true;
            this.onlyOneOfTypeAllowed = true;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(560f, 620f); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Widgets.DrawBoxSolid(inRect, AE2Draw.Panel);
            Widgets.DrawBox(inRect, 1);

            Rect r = new Rect(inRect.x + 8f, inRect.y + 6f, inRect.width - 16f, inRect.height - 12f);

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, r.width - 30f, 26f), "DS_BA_AddTitle".Translate());
            Text.Font = GameFont.Small;
            float y = r.y + 28f;

            // ---- 分类页签 ----
            float tabX = r.x;
            float tabY = y;
            for (int i = 0; i < CraftCategories.Tabs.Length; i++)
            {
                string label = CraftCategories.LabelKey(CraftCategories.Tabs[i]).Translate();
                float w = Mathf.Max(52f, Text.CalcSize(label).x + 16f);
                if (tabX + w > r.xMax)
                {
                    tabX = r.x;
                    tabY += 24f;
                }
                Rect tab = new Rect(tabX, tabY, w, 22f);
                if (catIndex == i) Widgets.DrawBoxSolid(tab, AE2Draw.Slot);
                else if (Mouse.IsOver(tab)) Widgets.DrawBoxSolid(tab, AE2Draw.Panel);
                GUI.color = (catIndex == i) ? AE2Draw.Accent : Color.white;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(tab, label);
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                if (Widgets.ButtonInvisible(tab)) { catIndex = i; scroll = Vector2.zero; }
                tabX += w + 3f;
            }
            y = tabY + 26f;

            Widgets.DrawBoxSolid(new Rect(r.x, y - 2f, r.width, 1f), AE2Draw.LineSoft);

            // ---- 搜索 ----
            search = Widgets.TextField(new Rect(r.x, y + 2f, r.width, 24f), search);
            y += 30f;

            // ---- 列表 ----
            BuildList();
            Rect listRect = new Rect(r.x, y, r.width, r.yMax - y - 26f);
            if (shown.Count == 0)
            {
                GUI.color = AE2Draw.TextDimCol;
                Widgets.Label(listRect, "DS_BA_NoRecipeAvailable".Translate());
                GUI.color = Color.white;
            }
            else
            {
                const float rowH = 36f;
                Rect inner = new Rect(0f, 0f, listRect.width - 18f, Math.Max(listRect.height, shown.Count * rowH + 4f));
                Widgets.BeginScrollView(listRect, ref scroll, inner);

                float ly = 0f;
                for (int i = 0; i < shown.Count; i++)
                {
                    RecipeDef recipe = shown[i];
                    Rect row = new Rect(2f, ly, inner.width - 4f, rowH - 2f);
                    bool added = comp.HasPlan(recipe);
                    bool skillOk = SkillOk(recipe);

                    if (Mouse.IsOver(row)) Widgets.DrawBoxSolid(row, AE2Draw.Slot);
                    else if (i % 2 == 0) Widgets.DrawBoxSolid(row, AE2Draw.Panel);

                    GUI.color = (added || !skillOk) ? AE2Draw.TextDimCol : Color.white;
                    ThingDef prod = CraftCategories.MainProduct(recipe);
                    if (prod != null) Widgets.DefIcon(new Rect(row.x + 3f, row.y + 3f, 26f, 26f), prod);
                    Widgets.Label(new Rect(row.x + 34f, row.y + 2f, row.width - 120f, 18f), recipe.LabelCap);
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(row.x + 34f, row.y + 19f, row.width - 120f, 16f), IngredientsLine(recipe));
                    Text.Font = GameFont.Small;
                    GUI.color = Color.white;

                    Text.Anchor = TextAnchor.MiddleRight;
                    if (added) GUI.color = AE2Draw.Accent;
                    else if (!skillOk) GUI.color = AE2Draw.Bad;
                    else GUI.color = AE2Draw.TextDimCol;
                    Widgets.Label(new Rect(row.xMax - 250f, row.y + 9f, 246f, 18f),
                        added ? "DS_BA_AlreadyAdded".Translate().ToString()
                              : (skillOk ? "DS_BA_ClickToAdd".Translate().ToString() : NeedSkillText(recipe)));
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;

                    // ★ 第 2 步（AE2 内核）+ 用户定案的两档操作：
                    //   · **Shift+点行** = 提交一个合成请求，整条中间产物链一起纳进依赖树
                    //     （走 CompBillAutomation.SubmitJob：CraftTree.Build 算整棵树 ⇒ 先建上游、最后建根）；
                    //   · **点行** = 只加这一条独立订单（`comp.AddPlan`，不进 Job、不占合成容量）——
                    //     这是上游的老习惯，保留给"我就想单独排一条"的场合。
                    if (!added && Widgets.ButtonInvisible(row))
                    {
                        if (Event.current != null && Event.current.shift) AddWholeTree(recipe);
                        else comp.AddPlan(recipe);
                    }
                    ly += rowH;
                }

                Widgets.EndScrollView();
            }

            GUI.color = AE2Draw.TextDimCol;
            Widgets.Label(new Rect(r.x, r.yMax - 22f, r.width, 20f), "DS_CA_AddHint".Translate());
            GUI.color = Color.white;
        }

        /// <summary>
        /// <b>连中间产物一起加</b>（Shift+点行）：交给内核的 <see cref="CompBillAutomation.SubmitJob"/>，
        /// 由它建整棵依赖树、逐个节点建订单（先上游、最后建根）。
        /// 界面只负责"报告结果"，不算树。
        ///
        /// <para>数量暂时固定 1（= 做 1 件最终产物）。要指定「维持 N 件 / 材料 / 风格」
        /// 请用 AE2 面板的「添加配方」（<c>Dialog_AE2AddRecipe</c>）。</para>
        /// </summary>
        private void AddWholeTree(RecipeDef recipe)
        {
            if (recipe == null) return;
            CraftJob job = comp.SubmitJob(recipe, null, 1, true);
            if (job == null) return;   // 被容量闸门（合成存储装不下）拒绝时，SubmitJob 已经给过提示

            if (job.steps.Count <= 1)
            {
                Messages.Message("DS_CT_AddTreeNone".Translate(), MessageTypeDefOf.NeutralEvent, historical: false);
                return;
            }
            Messages.Message("DS_JOB_Submitted".Translate(recipe.LabelCap, job.wanted, job.steps.Count),
                MessageTypeDefOf.NeutralEvent, historical: false);
        }

        private void BuildList()
        {
            shown.Clear();
            IList<RecipeDef> all = comp.UnlockedRecipes;
            CraftCategory want = CraftCategories.Tabs[Mathf.Clamp(catIndex, 0, CraftCategories.Tabs.Length - 1)];

            for (int i = 0; i < all.Count; i++)
            {
                RecipeDef r = all[i];
                if (r == null) continue;
                if (want != CraftCategory.All && CraftCategories.Of(r) != want) continue;

                if (!string.IsNullOrEmpty(search))
                {
                    string label = r.LabelCap.ToString();
                    bool hit = label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                        || r.defName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit) continue;
                }
                shown.Add(r);
            }
        }

        private static string IngredientsLine(RecipeDef r)
        {
            if (r.ingredients == null || r.ingredients.Count == 0) return "DS_CA_NoMaterials".Translate().ToString();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < r.ingredients.Count; i++)
            {
                if (i > 0) sb.Append(" + ");
                sb.Append(r.ingredients[i].Summary);
            }
            return sb.ToString();
        }

        private string NeedSkillText(RecipeDef r)
        {
            if (r.skillRequirements == null || r.skillRequirements.Count == 0) return "DS_BA_SkillShort".Translate().ToString();
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < r.skillRequirements.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(r.skillRequirements[i].skill.LabelCap).Append(' ').Append(r.skillRequirements[i].minLevel);
            }
            return "DS_BA_NeedSkill".Translate(sb.ToString()).ToString();
        }

        /// <summary>代理的固定资质够不够这个配方的 skillRequirements。</summary>
        private bool SkillOk(RecipeDef r)
        {
            if (r.skillRequirements == null) return true;
            int level = comp.Props.skillLevel;
            for (int i = 0; i < r.skillRequirements.Count; i++)
            {
                if (r.skillRequirements[i].minLevel > level) return false;
            }
            return true;
        }
    }
}
