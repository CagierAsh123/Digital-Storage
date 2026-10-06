// =====================================================================================
//  【本地新增文件／整块替换】ITab_DigitalStorage —— 存储核心内容物页签（AE2 材质版）
// -------------------------------------------------------------------------------------
//  这是**整块替换**：逻辑（按 def+材质+品质 聚合、6 组折叠、搜索、取出、搬运优先级、容量条）
//  与文案 key 全部保留，只把绘制换成 AE2 材质（AE2Draw）：
//    · 容量条 → AE2 凹槽 + 青色填充条（替掉 Widgets.FillableBar）
//    · 优先级 / 取出 → AE2 凸起按钮
//    · 搜索框 → AE2 输入框
//    · 列表 → AE2 行（分组头 + 行），**不再用 BeginScrollView**，改用 AE2Draw.DragBar 自绘滚动
//      （虚拟列表：先算每行的 y 位置，再只画落在可视区里的那些）
// =====================================================================================
using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    public class ITab_DigitalStorage : ITab
    {
        private static readonly Vector2 WinSize = new Vector2(470f, 560f);
        private readonly bool[] expanded = new bool[6];
        private float scrollFrac;          // ★ AE2：0~1 的滚动比例（DragBar 用）
        private string search = "";

        private const float HeaderRowH = 26f;
        private const float ItemRowH = 24f;

        private Building_StorageCore Core { get { return SelThing as Building_StorageCore; } }

        public ITab_DigitalStorage()
        {
            this.size = WinSize;
            this.labelKey = "DS_TabDigitalStorage";
        }

        public override bool IsVisible { get { return Core != null; } }

        // ===================================================================
        // 行快照：按 真实身份（def + 材质 + 品质）聚合（逻辑与原版一致）
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
            return (q != null) ? (int)q.Quality : -1;
        }

        private void RebuildRowsIfNeeded(Building_StorageCore core, bool force = false)
        {
            int tick = Find.TickManager.TicksGame;
            int count = core.innerContainer.Count;
            if (!force && tick == cachedTick && count == cachedCount) return;
            cachedTick = tick;
            cachedCount = count;

            List<ValueTuple<ThingDef, ThingDef, int>> order = new List<ValueTuple<ThingDef, ThingDef, int>>();
            Dictionary<ValueTuple<ThingDef, ThingDef, int>, int> counts = new Dictionary<ValueTuple<ThingDef, ThingDef, int>, int>();
            Dictionary<ValueTuple<ThingDef, ThingDef, int>, string> labels = new Dictionary<ValueTuple<ThingDef, ThingDef, int>, string>();

            for (int i = 0; i < core.innerContainer.Count; i++)
            {
                Thing t = core.innerContainer[i];
                if (t == null || t.def == null || t.Destroyed) continue;

                ValueTuple<ThingDef, ThingDef, int> key = new ValueTuple<ThingDef, ThingDef, int>(t.def, t.Stuff, QualityOrdinalOf(t));
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
                ValueTuple<ThingDef, ThingDef, int> key = order[i];
                Row r = new Row();
                r.def = key.Item1;
                r.stuff = key.Item2;
                r.qualityOrdinal = key.Item3;
                r.label = labels[key];
                r.count = counts[key];
                r.group = ItemGrouping.GroupOf(key.Item1);
                rows.Add(r);
            }
            rows.Sort(delegate (Row a, Row b) { return string.Compare(a.label, b.label, StringComparison.Ordinal); });
        }

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
            try
            {
            Building_StorageCore core = Core;
            if (core == null) return;
            RebuildRowsIfNeeded(core);

            AE2Draw.HandlePauseHotkey();   // ★ 用户要求：界面开着时空格也能暂停/继续
            Rect rect = new Rect(0f, 0f, WinSize.x, WinSize.y).ContractedBy(4f);
            AE2Draw.PanelBox(rect);
            Rect ri = rect.ContractedBy(6f);
            float y = ri.y;

            // ---- 容量条（AE2：凹槽 + 青色填充）----
            Rect barRect = new Rect(ri.x, y, ri.width - 100f, 22f);
            Rect craftBtn = new Rect(ri.xMax - 96f, y, 96f, 22f);
            if (AE2Draw.TextButton(craftBtn, "DS_AE2_OpenCraft".Translate().ToString(), true))
            {
                // 找不到代理就传 null ⇒ 新面板自己会挑一台；一台都没有会显示提示（原版文案）
                Find.WindowStack.Add(new Window_AE2CraftPanel(NearestProxy(core)));
            }
            // ★ 用户要求：右上角让出 100px 给"打开制作代理界面"按钮
            int used = core.innerContainer.Count;
            int cap = core.maxStacks;
            AE2Draw.Bar(barRect, (cap > 0) ? Mathf.Clamp01((float)used / cap) : 0f, false);
            AE2Draw.Tiny(new Rect(barRect.x, barRect.y + 3f, barRect.width, 16f),
                "DS_CapacityBarLv".Translate(CoreTier.Level, used, cap, rows.Count).ToString(), AE2Draw.Hi);
            y += 26f;

            // ---- 搬运优先级（AE2 按钮 + 浮窗菜单）----
            AE2Draw.Tiny(new Rect(ri.x, y + 3f, 54f, 16f), "DS_Priority".Translate().ToString() + ":", AE2Draw.TextCol);
            Rect prioRect = new Rect(ri.x + 56f, y, 150f, 22f);
            if (AE2Draw.TextButton(prioRect, PriorityLabel(core.storagePriority)))
            {
                List<FloatMenuOption> options = new List<FloatMenuOption>();
                foreach (StoragePriority p in Enum.GetValues(typeof(StoragePriority)))
                {
                    StoragePriority priority = p;
                    if (priority == StoragePriority.Unstored) continue;
                    options.Add(new FloatMenuOption(PriorityLabel(priority), delegate { core.storagePriority = priority; }));
                }
                Find.WindowStack.Add(new FloatMenu(options));
            }
            y += 26f;

            // ---- 搜索框（AE2 输入框）----
            search = AE2Draw.TextField(new Rect(ri.x, y, ri.width, 24f), search);
            y += 28f;

            // ---- 列表（虚拟列表 + AE2 侧面拖动条；不再用 BeginScrollView）----
            Rect list = new Rect(ri.x, y, ri.width, ri.yMax - y);
            DrawList(list, core);
            }
            catch (Exception __uiEx) { Log.ErrorOnce("[DigitalStorage] AE2 界面绘制异常（只记一次，界面不会卡死）：" + __uiEx, 771005); }
        }

        /// <summary>把 6 组 + 行铺成一条虚拟列表，只画落在可视区里的部分。</summary>
        private void DrawList(Rect list, Building_StorageCore core)
        {
            // ① 先排版：算出每一行的 y 与高度
            List<int> lineGroup = new List<int>();      // -1 = 行，>=0 = 组头（组号）
            List<int> lineRow = new List<int>();
            List<float> lineY = new List<float>();
            float total = 0f;

            for (int gi = 0; gi < 6; gi++)
            {
                ItemGroup group = (ItemGroup)gi;
                int kinds = 0;
                long gcount = 0;
                for (int ri2 = 0; ri2 < rows.Count; ri2++)
                {
                    if (rows[ri2].group != group || !RowMatchesSearch(rows[ri2])) continue;
                    kinds++;
                    gcount += rows[ri2].count;
                }
                if (kinds == 0) continue;

                lineGroup.Add(gi); lineRow.Add(-1); lineY.Add(total);
                total += HeaderRowH + 2f;
                if (expanded[gi])
                {
                    for (int ri2 = 0; ri2 < rows.Count; ri2++)
                    {
                        if (rows[ri2].group != group || !RowMatchesSearch(rows[ri2])) continue;
                        lineGroup.Add(-1); lineRow.Add(ri2); lineY.Add(total);
                        total += ItemRowH + 1f;
                    }
                }
            }

            if (total <= 0f)
            {
                AE2Draw.Tiny(new Rect(list.x + 4f, list.y + 4f, list.width - 8f, 18f),
                    (rows.Count == 0) ? "DS_EmptyCoreText".Translate().ToString() : "DS_KindsEmpty".Translate().ToString(),
                    AE2Draw.TextDimCol);
                return;
            }

            // ② 可视区 + 滚动偏移
            float viewH = list.height;
            float maxOffset = Mathf.Max(0f, total - viewH);
            float offset = Mathf.Round(scrollFrac * maxOffset);

            for (int i = 0; i < lineY.Count; i++)
            {
                float ly = lineY[i] - offset;
                if (ly + HeaderRowH < 0f) continue;
                if (ly > viewH) break;

                if (lineGroup[i] >= 0)
                {
                    int gi = lineGroup[i];
                    Rect header = new Rect(list.x, list.y + ly, list.width - 14f, HeaderRowH);
                    bool hover = Mouse.IsOver(header);
                    AE2Draw.Sunken(header, hover ? AE2Draw.SlotHover : AE2Draw.Slot);
                    int kinds = 0; long gcount = 0;
                    for (int ri2 = 0; ri2 < rows.Count; ri2++)
                    {
                        if (rows[ri2].group != (ItemGroup)gi || !RowMatchesSearch(rows[ri2])) continue;
                        kinds++; gcount += rows[ri2].count;
                    }
                    string arrow = expanded[gi] ? "▼" : "▶";
                    AE2Draw.Tiny(new Rect(header.x + 5f, header.y + 5f, header.width - 10f, 16f),
                        arrow + " " + ItemGrouping.LabelKeyOf((ItemGroup)gi).Translate().ToString()
                        + "   ×" + gcount + "   (" + kinds + " " + "DS_Kinds".Translate().ToString() + ")", AE2Draw.Hi);
                    if (Widgets.ButtonInvisible(header)) expanded[gi] = !expanded[gi];
                    continue;
                }

                Row row = rows[lineRow[i]];
                Rect line = new Rect(list.x + 10f, list.y + ly, list.width - 24f, ItemRowH);
                bool hov = Mouse.IsOver(line);
                AE2Draw.Row(line, hov, false);
                Rect icon = new Rect(line.x + 2f, line.y + 1f, 22f, 22f);
                AE2Draw.SlotBox(icon, false);
                Widgets.ThingIcon(icon.ContractedBy(1f), row.def, row.stuff);
                AE2Draw.Tiny(new Rect(line.x + 28f, line.y + 4f, line.width - 140f, 16f),
                    row.label + "   ×" + row.count, AE2Draw.TextCol);

                if (AE2Draw.TextButton(new Rect(line.xMax - 92f, line.y + 1f, 90f, 22f), "DS_WithdrawBtn".Translate().ToString()))
                {
                    Row captured = row;
                    Find.WindowStack.Add(new Dialog_WithdrawAmount(captured.label, captured.count, 0,
                        delegate (int amount) { ExtractRow(core, captured, amount); }));
                }
            }

            AE2Draw.DragBar(new Rect(list.xMax - 12f, list.y, 12f, list.height), ref scrollFrac,
                Mathf.Clamp01(viewH / Mathf.Max(1f, total)));
            AE2Draw.WheelScroll(list, ref scrollFrac);   // ★ 滚轮在列表里也能滚
        }

        private bool RowMatchesSearch(Row row)
        {
            if (string.IsNullOrEmpty(search)) return true;
            return row.label.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>把某一行（def + 材质 + 品质）取出 amount 个，落到核心旁边。</summary>
        private static void ExtractRow(Building_StorageCore core, Row row, int amount)
        {
            Map map = core.Map;
            if (map == null) return;

            int got = HaulSourceContents.ExtractMatchingTo(amount, core.Position, map,
                delegate (Thing t) { return t.def == row.def && t.Stuff == row.stuff && QualityOrdinalOf(t) == row.qualityOrdinal; });

            if (got <= 0)
                Messages.Message("DS_NoSpaceNearCore".Translate(), core, MessageTypeDefOf.RejectInput);
        }

        /// <summary>离这个核心最近的制作代理（找不到返回 null ⇒ 新面板会自己挑一台或给提示）。</summary>
        private static CompBillAutomation NearestProxy(Building_StorageCore core)
        {
            Map map = (core != null && core.Map != null) ? core.Map : Find.CurrentMap;
            if (map == null) return null;
            CompBillAutomation best = null;
            float bestD = float.MaxValue;
            List<Thing> all = map.listerThings.AllThings;
            for (int i = 0; i < all.Count; i++)
            {
                Thing th = all[i];
                if (th == null || !th.Spawned) continue;
                CompBillAutomation c = th.TryGetComp<CompBillAutomation>();
                if (c == null) continue;
                float d = (core != null) ? th.Position.DistanceTo(core.Position) : 0f;
                if (d < bestD) { bestD = d; best = c; }
            }
            return best;
        }

        private static string PriorityLabel(StoragePriority p)
        {
            // 键名必须与 vanilla Enums.xml 一致（StoragePriorityXxx），错键名会显示成泰南语
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
