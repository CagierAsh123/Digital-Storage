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
    /// 虚拟存储 UI：显示核心中的所有物品，支持搜索、滚动、手动提取
    /// </summary>
    public class Dialog_VirtualStorage : Window
    {
        private Building_StorageCore core;
        private string searchText = "";
        private Vector2 scrollPosition = Vector2.zero;
        private List<StoredItemData> filteredItems = new List<StoredItemData>();
        
        private const float RowHeight = 40f;
        private const float SearchBarHeight = 35f;
        private const float HeaderHeight = 40f;
        private const float ButtonWidth = 104f;
        private const float IconSize = 32f;

        public override Vector2 InitialSize => new Vector2(1120f, 600f);

        public Dialog_VirtualStorage(Building_StorageCore core)
        {
            this.core = core;
            this.doCloseButton = true;
            this.doCloseX = true;
            this.forcePause = false;
            this.absorbInputAroundWindow = true;
            UpdateFilteredItems();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            
            float curY = 0f;

            // 标题
            Rect titleRect = new Rect(0f, curY, inRect.width, HeaderHeight);
            Text.Font = GameFont.Medium;
            Widgets.Label(titleRect, "DS_VirtualStorageTitle".Translate(core.NetworkName));
            Text.Font = GameFont.Small;
            curY += HeaderHeight;

            // 容量信息
            Rect capacityRect = new Rect(0f, curY, inRect.width, 30f);
            string capacityText = "DS_CapacityInfo".Translate(core.GetUsedCapacity(), core.GetCapacity(), filteredItems.Count);
            Widgets.Label(capacityRect, capacityText);
            curY += 30f;

            // 搜索框
            Rect searchRect = new Rect(0f, curY, inRect.width - 100f, SearchBarHeight);
            string newSearchText = Widgets.TextField(searchRect, searchText);
            if (newSearchText != searchText)
            {
                searchText = newSearchText;
                UpdateFilteredItems();
            }

            // 清除搜索按钮
            Rect clearButtonRect = new Rect(inRect.width - 90f, curY, 90f, SearchBarHeight);
            if (Widgets.ButtonText(clearButtonRect, "DS_ClearSearch".Translate()))
            {
                searchText = "";
                UpdateFilteredItems();
            }
            curY += SearchBarHeight + 10f;

            // 列表标题
            Rect headerRect = new Rect(0f, curY, inRect.width, 30f);
            DrawListHeader(headerRect);
            curY += 30f;

            // 滚动列表
            Rect scrollViewRect = new Rect(0f, curY, inRect.width, inRect.height - curY - 50f);
            Rect scrollContentRect = new Rect(0f, 0f, scrollViewRect.width - 20f, filteredItems.Count * RowHeight);
            
            Widgets.BeginScrollView(scrollViewRect, ref scrollPosition, scrollContentRect);
            
            float itemY = 0f;
            for (int i = 0; i < filteredItems.Count; i++)
            {
                StoredItemData item = filteredItems[i];
                Rect rowRect = new Rect(0f, itemY, scrollContentRect.width, RowHeight);
                DrawItemRow(rowRect, item, i);
                itemY += RowHeight;
            }
            
            Widgets.EndScrollView();
        }

        private void DrawListHeader(Rect rect)
        {
            GUI.color = new Color(0.5f, 0.5f, 0.5f, 1f);
            Widgets.DrawLineHorizontal(rect.x, rect.y, rect.width);
            Widgets.DrawLineHorizontal(rect.x, rect.yMax, rect.width);
            GUI.color = Color.white;

            Text.Anchor = TextAnchor.MiddleLeft;
            
            Rect iconRect = new Rect(rect.x + 5f, rect.y, 50f, rect.height);
            Widgets.Label(iconRect, "DS_HeaderIcon".Translate());
            
            Rect nameRect = new Rect(rect.x + 60f, rect.y, 250f, rect.height);
            Widgets.Label(nameRect, "DS_HeaderName".Translate());
            
            Rect qualityRect = new Rect(rect.x + 320f, rect.y, 100f, rect.height);
            Widgets.Label(qualityRect, "DS_HeaderQuality".Translate());
            
            Rect countRect = new Rect(rect.x + 430f, rect.y, 100f, rect.height);
            Widgets.Label(countRect, "DS_HeaderCount".Translate());
            
            Rect hpRect = new Rect(rect.x + 540f, rect.y, 80f, rect.height);
            Widgets.Label(hpRect, "DS_HeaderDurability".Translate());
            
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private void DrawItemRow(Rect rect, StoredItemData item, int index)
        {
            if (index % 2 == 0)
            {
                Widgets.DrawLightHighlight(rect);
            }

            if (Mouse.IsOver(rect))
            {
                Widgets.DrawHighlight(rect);
            }

            Rect iconRect = new Rect(rect.x + 5f, rect.y + (rect.height - IconSize) / 2f, IconSize, IconSize);
            if (item.def != null)
            {
                Widgets.ThingIcon(iconRect, item.def, item.stuffDef);
                
                if (Mouse.IsOver(iconRect))
                {
                    TooltipHandler.TipRegion(iconRect, GetItemTooltip(item));
                }
            }

            Text.Anchor = TextAnchor.MiddleLeft;

            Rect nameRect = new Rect(rect.x + 60f, rect.y, 250f, rect.height);
            string label = GetItemLabel(item);
            Widgets.Label(nameRect, label);

            Rect qualityRect = new Rect(rect.x + 320f, rect.y, 100f, rect.height);
            if (item.quality != QualityCategory.Normal)
            {
                GUI.color = GetQualityColor(item.quality);
                Widgets.Label(qualityRect, item.quality.GetLabel());
                GUI.color = Color.white;
            }

            Rect countRect = new Rect(rect.x + 430f, rect.y, 100f, rect.height);
            Widgets.Label(countRect, item.stackCount.ToString());

            Rect hpRect = new Rect(rect.x + 540f, rect.y, 80f, rect.height);
            if (item.hitPoints > 0 && item.def != null)
            {
                float hpPercent = (float)item.hitPoints / item.def.BaseMaxHitPoints;
                string hpText = $"{Mathf.RoundToInt(hpPercent * 100)}%";
                
                if (hpPercent < 0.5f)
                {
                    GUI.color = Color.yellow;
                }
                if (hpPercent < 0.25f)
                {
                    GUI.color = Color.red;
                }
                
                Widgets.Label(hpRect, hpText);
                GUI.color = Color.white;
            }

            Text.Anchor = TextAnchor.UpperLeft;

            DrawExtractButtons(rect, item);
        }

        private void DrawExtractButtons(Rect rect, StoredItemData item)
        {
            float buttonX = rect.xMax - ButtonWidth * 3 - 15f;
            
            Rect extract1Rect = new Rect(buttonX, rect.y + 5f, ButtonWidth - 5f, rect.height - 10f);
            if (Widgets.ButtonText(extract1Rect, "DS_ExtractX1".Translate()))
            {
                ExtractItem(item, 1);
            }

            buttonX += ButtonWidth;
            Rect extractStackRect = new Rect(buttonX, rect.y + 5f, ButtonWidth - 5f, rect.height - 10f);
            int stackLimit = item.def?.stackLimit ?? 1;
            string stackLabel = "DS_Extract1Stack".Translate();
            
            if (item.stackCount >= stackLimit)
            {
                if (Widgets.ButtonText(extractStackRect, stackLabel))
                {
                    ExtractItem(item, stackLimit);
                }
            }
            else
            {
                GUI.color = Color.gray;
                Widgets.ButtonText(extractStackRect, stackLabel);
                GUI.color = Color.white;
            }

            buttonX += ButtonWidth;
            Rect extractCustomRect = new Rect(buttonX, rect.y + 5f, ButtonWidth - 5f, rect.height - 10f);
            if (Widgets.ButtonText(extractCustomRect, "DS_ExtractCustom".Translate()))
            {
                Find.WindowStack.Add(new Dialog_ExtractAmount(item, this));
            }
        }

        public void ExtractItemPublic(StoredItemData item, int count)
        {
            ExtractItem(item, count);
        }

        private void ExtractItem(StoredItemData item, int count)
        {
            if (core == null || !core.Spawned || item == null)
            {
                Messages.Message("DS_CoreUnavailable".Translate(), MessageTypeDefOf.RejectInput);
                return;
            }

            int actualCount = Mathf.Min(count, item.stackCount);
            int stackLimit = item.def.stackLimit;
            int totalExtracted = 0;

            while (actualCount > 0)
            {
                int batchCount = Mathf.Min(actualCount, stackLimit);
                Thing extractedThing = core.ExtractItem(item.def, batchCount, item.stuffDef);
                
                if (extractedThing == null)
                {
                    break;
                }

                // 在核心周围找空位生成物品
                IntVec3 spawnPos = FindSpawnPositionNear(core.Position, core.Map, 3);
                if (!spawnPos.IsValid)
                {
                    core.StoreItem(extractedThing);
                    Messages.Message("DS_NoSpaceNearCore".Translate(), MessageTypeDefOf.RejectInput);
                    break;
                }

                Thing spawnedThing = GenSpawn.Spawn(extractedThing, spawnPos, core.Map);
                FleckMaker.ThrowLightningGlow(spawnedThing.DrawPos, core.Map, 0.5f);
                
                totalExtracted += batchCount;
                actualCount -= batchCount;
            }

            if (totalExtracted > 0)
            {
                Messages.Message("DS_Extracted".Translate(GetItemLabel(item), totalExtracted), MessageTypeDefOf.TaskCompletion);
                UpdateFilteredItems();
            }
            else
            {
                Messages.Message("DS_ExtractionFailed".Translate(), MessageTypeDefOf.RejectInput);
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

        private string GetItemLabel(StoredItemData item)
        {
            if (item.def == null)
            {
                return "DS_UnknownItem".Translate();
            }

            string label = item.def.label;
            if (item.stuffDef != null)
            {
                label = item.stuffDef.label + label;
            }

            return label.CapitalizeFirst();
        }

        private string GetItemTooltip(StoredItemData item)
        {
            if (item.def == null)
            {
                return "DS_UnknownItem".Translate();
            }

            string tooltip = GetItemLabel(item);
            tooltip += "\n" + "DS_TooltipCount".Translate(item.stackCount);
            
            if (item.quality != QualityCategory.Normal)
            {
                tooltip += "\n" + "DS_TooltipQuality".Translate(item.quality.GetLabel());
            }
            
            if (item.hitPoints > 0 && item.def.useHitPoints)
            {
                float hpPercent = (float)item.hitPoints / item.def.BaseMaxHitPoints;
                tooltip += "\n" + "DS_TooltipDurability".Translate(item.hitPoints, item.def.BaseMaxHitPoints, Mathf.RoundToInt(hpPercent * 100));
            }

            if (!string.IsNullOrEmpty(item.def.description))
            {
                tooltip += $"\n\n{item.def.description}";
            }

            return tooltip;
        }

        private Color GetQualityColor(QualityCategory quality)
        {
            switch (quality)
            {
                case QualityCategory.Awful:
                    return new Color(0.5f, 0.5f, 0.5f);
                case QualityCategory.Poor:
                    return new Color(0.8f, 0.6f, 0.4f);
                case QualityCategory.Normal:
                    return Color.white;
                case QualityCategory.Good:
                    return new Color(0.6f, 0.8f, 0.6f);
                case QualityCategory.Excellent:
                    return new Color(0.4f, 0.8f, 1f);
                case QualityCategory.Masterwork:
                    return new Color(1f, 0.6f, 0.2f);
                case QualityCategory.Legendary:
                    return new Color(1f, 0.8f, 0.2f);
                default:
                    return Color.white;
            }
        }

        private void UpdateFilteredItems()
        {
            if (core == null)
            {
                filteredItems.Clear();
                return;
            }

            IEnumerable<StoredItemData> allItems = core.GetAllStoredItems();
            
            if (string.IsNullOrEmpty(searchText))
            {
                filteredItems = allItems.ToList();
            }
            else
            {
                string searchLower = searchText.ToLower();
                filteredItems = allItems.Where(item => 
                {
                    if (item.def == null)
                    {
                        return false;
                    }
                    
                    string label = GetItemLabel(item).ToLower();
                    return label.Contains(searchLower);
                }).ToList();
            }

            filteredItems = filteredItems.OrderBy(item => GetItemLabel(item)).ToList();
        }
    }
}

