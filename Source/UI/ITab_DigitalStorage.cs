using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// 存储核心的内容物面板。
    ///
    /// <para><b>4.0 改造</b>：3.0 这个面板读的是账本（<c>core.Ledger.Stock</c> 的
    /// <c>ItemKey</c> → 数量）。现在读的是 <c>core.innerContainer</c> —— **真实的 Thing**，
    /// 所以行按**真实身份**分组：<c>def + stuff + 品质</c>。</para>
    ///
    /// <para>这正是「全放开」的意义所在：同一 def 的普通剑和传奇剑是**两行**，
    /// 可以分别取出（3.0 的 <c>(def,stuff)</c> 键根本区分不出来）。</para>
    ///
    /// <para>6 组折叠展示沿用 <see cref="ItemGrouping"/>（原 <c>ItemGroup.cs</c> 保留，
    /// 它与账本无关，只是 def → 面板分组的映射）。</para>
    /// </summary>
    public class ITab_DigitalStorage : ITab
    {
        private static readonly Vector2 WinSize = new Vector2(460f, 540f);
        private readonly bool[] expanded = new bool[6];
        private Vector2 scroll;
        private string search = "";

        private Building_StorageCore Core => SelThing as Building_StorageCore;

        public ITab_DigitalStorage()
        {
            this.size = WinSize;
            this.labelKey = "DS_TabDigitalStorage";
        }

        public override bool IsVisible => Core != null;

        // ===================================================================
        // 行快照：按 真实身份（def + stuff + 品质）聚合
        // ===================================================================

        private struct Row
        {
            public ThingDef def;
            public ThingDef stuff;
            public int qualityOrdinal; // -1 = 无品质
            public string label;
            public int count;
            public ItemGroup group;
        }

        private readonly List<Row> rows = new List<Row>();
        private int cachedTick = -1;
        private int cachedCount = -1;

        private static int QualityOrdinalOf(Thing t)
        {
            CompQuality q = t.TryGetComp<CompQuality>();
            return q != null ? (int)q.Quality : -1;
        }

        /// <summary>
        /// 重建行快照。容器内容物只在取出/放入时变，所以按 (tick, 堆数) 做一次廉价的
        /// 变更检测即可 —— 避免每帧 6 遍全表扫描（旧实现有同样的 P8 缓存思路）。
        /// </summary>
        private void RebuildRowsIfNeeded(Building_StorageCore core, bool force = false)
        {
            int tick = Find.TickManager.TicksGame;
            int count = core.innerContainer.Count;
            if (!force && tick == cachedTick && count == cachedCount) return;
            cachedTick = tick;
            cachedCount = count;

            var order = new List<(ThingDef def, ThingDef stuff, int q)>();
            var counts = new Dictionary<(ThingDef, ThingDef, int), int>();
            var labels = new Dictionary<(ThingDef, ThingDef, int), string>();

            for (int i = 0; i < core.innerContainer.Count; i++)
            {
                Thing t = core.innerContainer[i];
                if (t?.def == null || t.Destroyed) continue;

                var key = (t.def, t.Stuff, QualityOrdinalOf(t));
                int cur;
                if (counts.TryGetValue(key, out cur))
                {
                    counts[key] = cur + t.stackCount;
                }
                else
                {
                    counts[key] = t.stackCount;
                    labels[key] = BuildLabel(t);
                    order.Add(key);
                }
            }

            rows.Clear();
            for (int i = 0; i < order.Count; i++)
            {
                var key = order[i];
                rows.Add(new Row
                {
                    def = key.def,
                    stuff = key.stuff,
                    qualityOrdinal = key.q,
                    label = labels[key],
                    count = counts[key],
                    group = ItemGrouping.GroupOf(key.def),
                });
            }
            rows.Sort((a, b) => string.Compare(a.label, b.label, StringComparison.Ordinal));
        }

        /// <summary>
        /// 行标签。带 <c>try/catch</c>：某些 def 的 <c>LabelNoCount</c> 会抛
        /// （3.0 的 LedgerItemCollector 就为这个专门加了防护），UI 不能因此崩掉。
        /// 顺带附上耐久百分比 —— 「全放开」让耐久有意义的物品（武器/护甲）能一眼区分。
        /// </summary>
        private static string BuildLabel(Thing t)
        {
            string label;
            try { label = t.LabelNoCount; }
            catch { label = t.def.LabelCap; }

            if (t.def.useHitPoints && t.MaxHitPoints > 0)
                label += "  " + ((float)t.HitPoints / t.MaxHitPoints).ToStringPercent();
            return label;
        }

        // ===================================================================

        protected override void FillTab()
        {
            Building_StorageCore core = Core;
            if (core == null) return;
            RebuildRowsIfNeeded(core);

            Rect rect = new Rect(0f, 0f, WinSize.x, WinSize.y).ContractedBy(10f);
            Text.Font = GameFont.Small;
            float y = 0f;

            // 顶部：堆数占用条（上限 = maxStacks，由研究阶梯决定：Lv1 500 → Lv4 3000）
            // 「堆」与「种」是两个口径，必须都写出来：
            //   堆 = 容器里真实 Thing 的个数（能合并的算一堆）；stackLimit = 1 的东西（石块/武器/衣物）一件就是一堆。
            //   种 = 按 def + 材质 + 品质 聚合出来的行数（下面分组标题里的那个"种"）。
            Rect barRect = new Rect(rect.x, rect.y + y, rect.width, 22f);
            int used = core.innerContainer.Count;
            int cap = core.maxStacks;
            // FillableBar 自己不 clamp（内部就一句 rect.width *= fillPercent，Widgets.cs:2555）
            // ⇒ 旧存档里已经超上限的核心会把条画到面板外，这里自己夹住。
            Widgets.FillableBar(barRect, cap > 0 ? Mathf.Clamp01((float)used / cap) : 0f);
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(barRect, "DS_CapacityBarLv".Translate(CoreTier.Level, used, cap, rows.Count));
            Text.Anchor = TextAnchor.UpperLeft;
            y += 26f;

            // 搬运优先级
            Widgets.Label(new Rect(rect.x, rect.y + y, 60f, 22f), "DS_Priority".Translate() + ":");
            Rect prioRect = new Rect(rect.x + 62f, rect.y + y, 140f, 22f);
            if (Widgets.ButtonText(prioRect, PriorityLabel(core.storagePriority)))
            {
                var options = new List<FloatMenuOption>();
                foreach (StoragePriority p in Enum.GetValues(typeof(StoragePriority)))
                {
                    StoragePriority priority = p;
                    if (priority == StoragePriority.Unstored) continue;
                    options.Add(new FloatMenuOption(PriorityLabel(priority), delegate
                    {
                        core.storagePriority = priority;
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            y += 26f;

            // 搜索框
            search = Widgets.TextField(new Rect(rect.x, rect.y + y, rect.width, 24f), search);
            y += 30f;

            // 列表区
            Rect listOuter = new Rect(rect.x, rect.y + y, rect.width, rect.height - y);
            Rect listInner = new Rect(0, 0, listOuter.width - 16f, CalcListHeight());
            Widgets.BeginScrollView(listOuter, ref scroll, listInner);

            float ly = 0f;
            for (int gi = 0; gi < 6; gi++)
            {
                ItemGroup group = (ItemGroup)gi;

                int gkinds = 0;
                long gcount = 0;
                for (int ri = 0; ri < rows.Count; ri++)
                {
                    if (rows[ri].group != group) continue;
                    if (!RowMatchesSearch(rows[ri])) continue;
                    gkinds++;
                    gcount += rows[ri].count;
                }
                if (gkinds == 0) continue;

                Rect header = new Rect(0, ly, listInner.width, 26f);
                if (Mouse.IsOver(header)) Widgets.DrawHighlight(header);
                string arrow = expanded[gi] ? "▼" : "▶";
                Widgets.Label(header, arrow + " " + ItemGrouping.LabelKeyOf(group).Translate()
                    + "   x" + gcount + "   (" + gkinds + " " + "DS_Kinds".Translate() + ")");
                if (Widgets.ButtonInvisible(header)) expanded[gi] = !expanded[gi];
                ly += 28f;

                if (!expanded[gi]) continue;

                for (int ri = 0; ri < rows.Count; ri++)
                {
                    Row row = rows[ri];
                    if (row.group != group || !RowMatchesSearch(row)) continue;

                    Rect line = new Rect(12f, ly, listInner.width - 12f, 24f);
                    if (Mouse.IsOver(line)) Widgets.DrawHighlight(line);

                    Widgets.ThingIcon(new Rect(line.x, line.y, 22f, 22f), row.def, row.stuff);
                    Widgets.Label(new Rect(line.x + 26f, line.y, line.width - 130f, 24f),
                        row.label + "   x" + row.count);

                    if (Widgets.ButtonText(new Rect(line.xMax - 100f, line.y, 100f, 22f), "DS_WithdrawBtn".Translate()))
                    {
                        Row captured = row;
                        Find.WindowStack.Add(new Dialog_WithdrawAmount(captured.label, captured.count, 0,
                            amount => ExtractRow(core, captured, amount)));
                    }

                    ly += 26f;
                }
            }

            Widgets.EndScrollView();
        }

        private bool RowMatchesSearch(Row row)
        {
            if (string.IsNullOrEmpty(search)) return true;
            return row.label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>把某一行（def + stuff + 品质）取出 amount 个，落到核心旁边。</summary>
        private static void ExtractRow(Building_StorageCore core, Row row, int amount)
        {
            Map map = core.Map;
            if (map == null) return;

            int got = HaulSourceContents.ExtractMatchingTo(core, amount, core.Position, map,
                t => t.def == row.def && t.Stuff == row.stuff && QualityOrdinalOf(t) == row.qualityOrdinal);

            if (got <= 0)
                Messages.Message("DS_NoSpaceNearCore".Translate(), core, MessageTypeDefOf.RejectInput);
        }

        private float CalcListHeight()
        {
            float h = 0f;
            for (int gi = 0; gi < 6; gi++)
            {
                int gkinds = 0;
                for (int ri = 0; ri < rows.Count; ri++)
                    if (rows[ri].group == (ItemGroup)gi && RowMatchesSearch(rows[ri])) gkinds++;
                if (gkinds == 0) continue;
                h += 28f;
                if (expanded[gi]) h += gkinds * 26f;
            }
            return h;
        }

        private static string PriorityLabel(StoragePriority p)
        {
            // 键名必须与 vanilla Enums.xml 一致(StoragePriorityXxx,见 StoragePriorityHelper)，
            // 错误键名(PriorityXxx)会被 Translate() 当缺失键 → 显示乱码(泰南语)
            switch (p)
            {
                case StoragePriority.Low: return "StoragePriorityLow".Translate();
                case StoragePriority.Normal: return "StoragePriorityNormal".Translate();
                case StoragePriority.Preferred: return "StoragePriorityPreferred".Translate();
                case StoragePriority.Important: return "StoragePriorityImportant".Translate();
                case StoragePriority.Critical: return "StoragePriorityCritical".Translate();
                default: return p.ToString();
            }
        }
    }
}
