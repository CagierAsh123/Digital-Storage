using System;
using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Components;
using DigitalStorage.Data;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// ITab: 虚拟存储内容标签页，嵌入 inspect 面板
    /// </summary>
    public class ITab_VirtualStorage : ITab
    {
        private Vector2 scrollPosition = Vector2.zero;
        private string searchText = "";
        private List<StoredItemData> cachedItems = new List<StoredItemData>();
        private List<Thing> cachedPendingThings = new List<Thing>();
        private int lastUpdateTick = -1;

        private const float ROW_HEIGHT = 30f;
        private const float ICON_SIZE = 28f;
        private const float SEARCH_HEIGHT = 30f;
        private const float HEADER_HEIGHT = 24f;
        private const float BORDER_MARGIN = 10f;
        private const float TOP_PADDING = 6f;
        private const float BUTTON_SIZE = 24f;
        private const float LABEL_MIN_GAP = 4f;
        private const float MASS_COL_WIDTH = 60f;
        private const float COUNT_COL_WIDTH = 70f;
        private const float SECTION_HEADER_HEIGHT = 22f;

        private static readonly Color ThingHighlightColor = new Color(1f, 1f, 1f, 0.15f);
        private static readonly Color PendingSectionColor = new Color(1f, 0.85f, 0.4f, 0.8f);
        private static readonly Color PendingRowBgColor = new Color(1f, 0.9f, 0.5f, 0.08f);

        public ITab_VirtualStorage()
        {
            this.size = new Vector2(460f, 480f);
            this.labelKey = "DS_TabVirtualStorage";
        }

        public override bool IsVisible
        {
            get
            {
                Building_StorageCore core = this.SelThing as Building_StorageCore;
                return core != null;
            }
        }

        private Building_StorageCore SelCore => this.SelThing as Building_StorageCore;

        protected override void FillTab()
        {
            Building_StorageCore core = SelCore;
            if (core == null) return;

            UpdateCachedItems(core);

            Rect outRect = new Rect(Vector2.zero, this.size).ContractedBy(BORDER_MARGIN);
            outRect.yMin += TOP_PADDING;

            Text.Font = GameFont.Small;
            float curY = outRect.y;

            // Header: "虚拟存储 (123/500 组)"
            DrawHeader(ref outRect, ref curY, core);

            // Search bar
            DrawSearchBar(ref outRect, ref curY);

            // Scroll list
            Rect scrollOutRect = new Rect(outRect.x, curY, outRect.width, outRect.yMax - curY);
            float pendingHeight = cachedPendingThings.Count > 0 ? SECTION_HEADER_HEIGHT + cachedPendingThings.Count * ROW_HEIGHT + 4f : 0f;
            float virtualHeight = cachedItems.Count > 0 ? SECTION_HEADER_HEIGHT + cachedItems.Count * ROW_HEIGHT : 0f;
            float totalHeight = pendingHeight + virtualHeight;
            if (totalHeight < 40f) totalHeight = 40f;
            Rect scrollContentRect = new Rect(0f, 0f, scrollOutRect.width - 16f, totalHeight);

            Widgets.BeginScrollView(scrollOutRect, ref scrollPosition, scrollContentRect);

            float itemY = 0f;
            bool hasAny = false;

            // === 等待吸收区域 ===
            if (cachedPendingThings.Count > 0)
            {
                hasAny = true;

                // Section header
                Rect pendingHeaderRect = new Rect(0f, itemY, scrollContentRect.width, SECTION_HEADER_HEIGHT);
                GUI.color = PendingSectionColor;
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(pendingHeaderRect, "DS_PendingSection".Translate(cachedPendingThings.Count));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                itemY += SECTION_HEADER_HEIGHT;

                for (int i = 0; i < cachedPendingThings.Count; i++)
                {
                    Thing t = cachedPendingThings[i];
                    if (t == null || t.Destroyed) continue;

                    // Cull invisible rows
                    if (itemY + ROW_HEIGHT >= scrollPosition.y && itemY <= scrollPosition.y + scrollOutRect.height)
                    {
                        Rect rowRect = new Rect(0f, itemY, scrollContentRect.width, ROW_HEIGHT);
                        DrawPendingThingRow(rowRect, t, i);
                    }
                    itemY += ROW_HEIGHT;
                }
                itemY += 4f;
            }

            // === 虚拟存储区域 ===
            if (cachedItems.Count > 0)
            {
                hasAny = true;

                // Section header
                Rect virtualHeaderRect = new Rect(0f, itemY, scrollContentRect.width, SECTION_HEADER_HEIGHT);
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(virtualHeaderRect, "DS_VirtualSection".Translate(cachedItems.Count));
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                itemY += SECTION_HEADER_HEIGHT;
            }

            for (int i = 0; i < cachedItems.Count; i++)
            {
                StoredItemData item = cachedItems[i];
                if (item == null || item.def == null) continue;

                hasAny = true;

                // Cull invisible rows
                if (itemY + ROW_HEIGHT < scrollPosition.y || itemY > scrollPosition.y + scrollOutRect.height)
                {
                    itemY += ROW_HEIGHT;
                    continue;
                }

                Rect rowRect = new Rect(0f, itemY, scrollContentRect.width, ROW_HEIGHT);
                DrawItemRow(rowRect, item, i, core);
                itemY += ROW_HEIGHT;
            }

            if (!hasAny)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = Color.gray;
                Widgets.Label(new Rect(0f, 0f, scrollContentRect.width, 40f), "DS_EmptyStorage".Translate());
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
            }

            Widgets.EndScrollView();
        }

        private void DrawHeader(ref Rect outRect, ref float curY, Building_StorageCore core)
        {
            Rect headerRect = new Rect(outRect.x, curY, outRect.width, HEADER_HEIGHT);

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            string headerText = "DS_TabHeader".Translate(core.GetUsedCapacity(), core.GetCapacity());
            Widgets.Label(headerRect, headerText);
            Text.Anchor = TextAnchor.UpperLeft;

            curY += HEADER_HEIGHT + 2f;
        }

        private void DrawSearchBar(ref Rect outRect, ref float curY)
        {
            Rect searchRect = new Rect(outRect.x, curY, outRect.width, SEARCH_HEIGHT);
            string newSearch = Widgets.TextField(searchRect, searchText);
            if (newSearch != searchText)
            {
                searchText = newSearch;
                lastUpdateTick = -1; // force refresh
            }
            curY += SEARCH_HEIGHT + 4f;
        }

        private void DrawPendingThingRow(Rect rect, Thing thing, int index)
        {
            // Pending background tint
            GUI.color = PendingRowBgColor;
            GUI.DrawTexture(rect, TexUI.HighlightTex);
            GUI.color = Color.white;

            // Alternating background
            if ((index & 1) == 0)
            {
                Widgets.DrawAltRect(rect);
            }

            // Hover highlight
            if (Mouse.IsOver(rect))
            {
                GUI.color = ThingHighlightColor;
                GUI.DrawTexture(rect, TexUI.HighlightTex);
                GUI.color = Color.white;
            }

            float x = rect.x;

            // Icon
            Rect iconRect = new Rect(x + 2f, rect.y + (rect.height - ICON_SIZE) / 2f, ICON_SIZE, ICON_SIZE);
            Widgets.ThingIcon(iconRect, thing);
            x += ICON_SIZE + 4f;

            // Info card button from right
            float rightX = rect.xMax;
            rightX -= BUTTON_SIZE;
            Widgets.InfoCardButton(rightX, rect.y + (rect.height - BUTTON_SIZE) / 2f, thing);
            rightX -= 4f;

            // Count
            rightX -= COUNT_COL_WIDTH;
            Rect countRect = new Rect(rightX, rect.y, COUNT_COL_WIDTH, rect.height);
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(countRect, thing.stackCount.ToString("N0"));
            rightX -= 2f;

            // Label
            float labelWidth = rightX - x - LABEL_MIN_GAP;
            Rect labelRect = new Rect(x, rect.y, labelWidth, rect.height);
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;
            GUI.color = PendingSectionColor;
            Widgets.Label(labelRect, thing.LabelCapNoCount);
            GUI.color = Color.white;
            Text.WordWrap = true;
            Text.Anchor = TextAnchor.UpperLeft;

            // Tooltip
            TooltipHandler.TipRegion(rect, "DS_PendingTooltip".Translate(thing.LabelCap, thing.stackCount));
        }

        private void DrawItemRow(Rect rect, StoredItemData item, int index, Building_StorageCore core)
        {
            // Alternating background
            if ((index & 1) == 0)
            {
                Widgets.DrawAltRect(rect);
            }

            // Hover highlight
            if (Mouse.IsOver(rect))
            {
                GUI.color = ThingHighlightColor;
                GUI.DrawTexture(rect, TexUI.HighlightTex);
                GUI.color = Color.white;
            }

            float x = rect.x;

            // Icon
            Rect iconRect = new Rect(x + 2f, rect.y + (rect.height - ICON_SIZE) / 2f, ICON_SIZE, ICON_SIZE);
            Widgets.ThingIcon(iconRect, item.def, item.stuffDef);
            if (Mouse.IsOver(iconRect))
            {
                TooltipHandler.TipRegion(iconRect, GetItemTooltip(item));
            }
            x += ICON_SIZE + 4f;

            // Buttons from right side
            float rightX = rect.xMax;

            // Drop specific count button
            rightX -= BUTTON_SIZE;
            Rect dropCountRect = new Rect(rightX, rect.y + (rect.height - BUTTON_SIZE) / 2f, BUTTON_SIZE, BUTTON_SIZE);
            TooltipHandler.TipRegion(dropCountRect, "DS_ExtractCustom".Translate());
            if (Widgets.ButtonImage(dropCountRect, TexButton.Paste))
            {
                int maxCount = (int)Math.Min(item.stackCount, int.MaxValue);
                Find.WindowStack.Add(new Dialog_Slider(
                    "DS_ExtractSlider".Translate(GetItemLabel(item)),
                    1, maxCount,
                    count => ExtractItem(core, item, count)));
            }

            // Drop all button
            rightX -= BUTTON_SIZE;
            Rect dropAllRect = new Rect(rightX, rect.y + (rect.height - BUTTON_SIZE) / 2f, BUTTON_SIZE, BUTTON_SIZE);
            TooltipHandler.TipRegion(dropAllRect, "DS_ExtractAll".Translate());
            if (Widgets.ButtonImage(dropAllRect, TexButton.Drop))
            {
                int maxCount = (int)Math.Min(item.stackCount, int.MaxValue);
                ExtractItem(core, item, maxCount);
            }

            // Info card button
            rightX -= BUTTON_SIZE;
            Widgets.InfoCardButton(rightX, rect.y + (rect.height - BUTTON_SIZE) / 2f, item.def);

            rightX -= 4f;

            // Count (from right)
            rightX -= COUNT_COL_WIDTH;
            Rect countRect = new Rect(rightX, rect.y, COUNT_COL_WIDTH, rect.height);
            Text.Anchor = TextAnchor.MiddleRight;
            string countStr = FormatCount(item.stackCount);
            Widgets.Label(countRect, countStr);

            rightX -= 2f;

            // Label (fill remaining space)
            float labelWidth = rightX - x - LABEL_MIN_GAP;
            Rect labelRect = new Rect(x, rect.y, labelWidth, rect.height);

            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;
            GUI.color = GetQualityColor(item.quality);
            Widgets.Label(labelRect, GetItemLabel(item));
            GUI.color = Color.white;
            Text.WordWrap = true;
            Text.Anchor = TextAnchor.UpperLeft;

            // Full row tooltip
            TooltipHandler.TipRegion(rect, GetItemTooltip(item));
        }

        private void ExtractItem(Building_StorageCore core, StoredItemData item, int count)
        {
            if (core == null || !core.Spawned || item == null || item.def == null)
            {
                Messages.Message("DS_CoreUnavailable".Translate(), MessageTypeDefOf.RejectInput);
                return;
            }

            int stackLimit = item.def.stackLimit;
            int remaining = count;
            int totalExtracted = 0;

            while (remaining > 0)
            {
                int batch = Math.Min(remaining, stackLimit);
                Thing extracted = core.ExtractItem(item.def, batch, item.stuffDef);
                if (extracted == null) break;

                IntVec3 spawnPos = FindSpawnPositionNear(core.Position, core.Map, 3);
                if (!spawnPos.IsValid)
                {
                    core.StoreItem(extracted);
                    Messages.Message("DS_NoSpaceNearCore".Translate(), MessageTypeDefOf.RejectInput);
                    break;
                }

                GenSpawn.Spawn(extracted, spawnPos, core.Map);
                FleckMaker.ThrowLightningGlow(extracted.DrawPos, core.Map, 0.5f);

                totalExtracted += batch;
                remaining -= batch;
            }

            if (totalExtracted > 0)
            {
                Messages.Message("DS_Extracted".Translate(GetItemLabel(item), totalExtracted), MessageTypeDefOf.TaskCompletion);
                lastUpdateTick = -1; // force refresh
            }
        }

        private IntVec3 FindSpawnPositionNear(IntVec3 center, Map map, int radius)
        {
            for (int r = 0; r <= radius; r++)
            {
                foreach (IntVec3 cell in GenRadial.RadialCellsAround(center, r, true))
                {
                    if (cell.InBounds(map) && cell.Standable(map) && map.thingGrid.ThingsListAt(cell).Count == 0)
                    {
                        return cell;
                    }
                }
            }
            return IntVec3.Invalid;
        }

        private void UpdateCachedItems(Building_StorageCore core)
        {
            int tick = Find.TickManager.TicksGame;
            if (tick == lastUpdateTick) return;
            lastUpdateTick = tick;

            IEnumerable<StoredItemData> allItems = core.GetAllStoredItems();

            if (!string.IsNullOrEmpty(searchText))
            {
                string lower = searchText.ToLower();
                cachedItems = allItems
                    .Where(item => item != null && item.def != null && GetItemLabel(item).ToLower().Contains(lower))
                    .OrderBy(item => GetItemLabel(item))
                    .ToList();
            }
            else
            {
                cachedItems = allItems
                    .Where(item => item != null && item.def != null)
                    .OrderBy(item => GetItemLabel(item))
                    .ToList();
            }

            // 缓存格子上等待吸收的物理物品
            cachedPendingThings.Clear();
            SlotGroup sg = core.GetSlotGroup();
            if (sg != null)
            {
                foreach (Thing t in sg.HeldThings)
                {
                    if (t != null && !t.Destroyed)
                    {
                        if (string.IsNullOrEmpty(searchText) || t.LabelCap.ToString().ToLower().Contains(searchText.ToLower()))
                        {
                            cachedPendingThings.Add(t);
                        }
                    }
                }
            }
        }

        private static string GetItemLabel(StoredItemData item)
        {
            if (item.def == null) return "?";
            string label = item.def.label;
            if (item.stuffDef != null)
            {
                label = item.stuffDef.label + label;
            }
            return label.CapitalizeFirst();
        }

        private static string GetItemTooltip(StoredItemData item)
        {
            if (item.def == null) return "";

            string tip = GetItemLabel(item);
            tip += "\n" + "DS_TooltipCount".Translate(FormatCount(item.stackCount));

            if (item.quality != QualityCategory.Normal)
            {
                tip += "\n" + "DS_TooltipQuality".Translate(item.quality.GetLabel());
            }

            if (item.hitPoints > 0 && item.def.useHitPoints)
            {
                float pct = (float)item.hitPoints / item.def.BaseMaxHitPoints;
                tip += "\n" + "DS_TooltipDurability".Translate(item.hitPoints, item.def.BaseMaxHitPoints, Mathf.RoundToInt(pct * 100));
            }

            if (!string.IsNullOrEmpty(item.def.description))
            {
                tip += "\n\n" + item.def.description;
            }

            return tip;
        }

        private static string FormatCount(long count)
        {
            if (count >= 1000000)
                return (count / 1000000f).ToString("0.#") + "M";
            if (count >= 10000)
                return (count / 1000f).ToString("0.#") + "k";
            return count.ToString("N0");
        }

        private static Color GetQualityColor(QualityCategory quality)
        {
            switch (quality)
            {
                case QualityCategory.Awful: return new Color(0.55f, 0.55f, 0.55f);
                case QualityCategory.Poor: return new Color(0.8f, 0.6f, 0.4f);
                case QualityCategory.Good: return new Color(0.6f, 0.8f, 0.6f);
                case QualityCategory.Excellent: return new Color(0.4f, 0.8f, 1f);
                case QualityCategory.Masterwork: return new Color(1f, 0.6f, 0.2f);
                case QualityCategory.Legendary: return new Color(1f, 0.8f, 0.2f);
                default: return Color.white;
            }
        }
    }
}
