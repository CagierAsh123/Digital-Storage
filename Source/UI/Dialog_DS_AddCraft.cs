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
        private static readonly Color ColPanelDark = new Color(0.106f, 0.125f, 0.145f);
        private static readonly Color ColPanel = new Color(0.133f, 0.153f, 0.176f);
        private static readonly Color ColLite = new Color(0.173f, 0.200f, 0.227f);
        private static readonly Color ColLine = new Color(0.212f, 0.239f, 0.271f);
        private static readonly Color ColDim = new Color(0.529f, 0.561f, 0.596f);
        private static readonly Color ColBad = new Color(0.769f, 0.333f, 0.247f);
        private static readonly Color ColAccent = new Color(0.788f, 0.663f, 0.380f);

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
            Widgets.DrawBoxSolid(inRect, ColPanelDark);
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
                if (catIndex == i) Widgets.DrawBoxSolid(tab, ColLite);
                else if (Mouse.IsOver(tab)) Widgets.DrawBoxSolid(tab, ColPanel);
                GUI.color = (catIndex == i) ? ColAccent : Color.white;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(tab, label);
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                if (Widgets.ButtonInvisible(tab)) { catIndex = i; scroll = Vector2.zero; }
                tabX += w + 3f;
            }
            y = tabY + 26f;

            Widgets.DrawBoxSolid(new Rect(r.x, y - 2f, r.width, 1f), ColLine);

            // ---- 搜索 ----
            search = Widgets.TextField(new Rect(r.x, y + 2f, r.width, 24f), search);
            y += 30f;

            // ---- 列表 ----
            BuildList();
            Rect listRect = new Rect(r.x, y, r.width, r.yMax - y - 26f);
            if (shown.Count == 0)
            {
                GUI.color = ColDim;
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

                    if (Mouse.IsOver(row)) Widgets.DrawBoxSolid(row, ColLite);
                    else if (i % 2 == 0) Widgets.DrawBoxSolid(row, ColPanel);

                    GUI.color = (added || !skillOk) ? ColDim : Color.white;
                    ThingDef prod = CraftCategories.MainProduct(recipe);
                    if (prod != null) Widgets.DefIcon(new Rect(row.x + 3f, row.y + 3f, 26f, 26f), prod);
                    Widgets.Label(new Rect(row.x + 34f, row.y + 2f, row.width - 120f, 18f), recipe.LabelCap);
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(row.x + 34f, row.y + 19f, row.width - 120f, 16f), IngredientsLine(recipe));
                    Text.Font = GameFont.Small;
                    GUI.color = Color.white;

                    Text.Anchor = TextAnchor.MiddleRight;
                    if (added) GUI.color = ColAccent;
                    else if (!skillOk) GUI.color = ColBad;
                    else GUI.color = ColDim;
                    Widgets.Label(new Rect(row.xMax - 250f, row.y + 9f, 246f, 18f),
                        added ? "DS_BA_AlreadyAdded".Translate().ToString()
                              : (skillOk ? "DS_BA_ClickToAdd".Translate().ToString() : NeedSkillText(recipe)));
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;

                    // ★ 第 2 步（AE2 内核）：点一下 = **提交一个合成请求**，整条中间产物链一起纳进这棵依赖树
                    //   （以前是"只加这一条配方"，产物的上游得自己再一条条加 —— 那正是"只能做一种任务"）。
                    if (!added && Widgets.ButtonInvisible(row))
                    {
                        CraftJob job = comp.SubmitJob(recipe, null, 1, true);
                        if (job != null)
                        {
                            Messages.Message("DS_JOB_Submitted".Translate(recipe.LabelCap, 1, job.steps.Count),
                                MessageTypeDefOf.NeutralEvent, historical: false);
                        }
                    }
                    ly += rowH;
                }

                Widgets.EndScrollView();
            }

            GUI.color = ColDim;
            Widgets.Label(new Rect(r.x, r.yMax - 22f, r.width, 20f), "DS_CA_AddHint".Translate());
            GUI.color = Color.white;
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
