using DigitalStorage.Data;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    /// <summary>
    /// 自定义提取数量对话框
    /// </summary>
    public class Dialog_ExtractAmount : Window
    {
        private StoredItemData item;
        private Dialog_VirtualStorage parentDialog;
        private string amountBuffer;
        private int maxAmount;

        public override Vector2 InitialSize => new Vector2(300f, 260f);

        public Dialog_ExtractAmount(StoredItemData item, Dialog_VirtualStorage parentDialog)
        {
            this.item = item;
            this.parentDialog = parentDialog;
            this.maxAmount = Mathf.Min(item.stackCount, item.def?.stackLimit ?? 1);
            this.amountBuffer = this.maxAmount.ToString();
            
            this.doCloseButton = false;
            this.doCloseX = true;
            this.forcePause = true;
            this.absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            
            float curY = 0f;

            Rect titleRect = new Rect(0f, curY, inRect.width, 40f);
            Text.Font = GameFont.Medium;
            Widgets.Label(titleRect, "提取数量");
            Text.Font = GameFont.Small;
            curY += 50f;

            Rect itemLabelRect = new Rect(0f, curY, inRect.width, 30f);
            string itemLabel = GetItemLabel();
            Widgets.Label(itemLabelRect, $"物品: {itemLabel}");
            curY += 35f;

            Rect availableRect = new Rect(0f, curY, inRect.width, 30f);
            Widgets.Label(availableRect, $"可用: {item.stackCount}  (堆叠上限: {item.def?.stackLimit ?? 1})");
            curY += 35f;

            Rect inputRect = new Rect(0f, curY, inRect.width - 20f, 30f);
            amountBuffer = Widgets.TextField(inputRect, amountBuffer);
            curY += 40f;

            float buttonWidth = (inRect.width - 10f) / 2f;
            
            Rect confirmRect = new Rect(0f, curY, buttonWidth, 35f);
            if (Widgets.ButtonText(confirmRect, "确认"))
            {
                TryExtract();
            }

            Rect cancelRect = new Rect(buttonWidth + 10f, curY, buttonWidth, 35f);
            if (Widgets.ButtonText(cancelRect, "取消"))
            {
                Close();
            }
        }

        private void TryExtract()
        {
            if (!int.TryParse(amountBuffer, out int amount))
            {
                Messages.Message("请输入有效的数字", MessageTypeDefOf.RejectInput);
                return;
            }

            if (amount <= 0)
            {
                Messages.Message("数量必须大于 0", MessageTypeDefOf.RejectInput);
                return;
            }

            if (amount > maxAmount)
            {
                Messages.Message($"数量不能超过 {maxAmount}", MessageTypeDefOf.RejectInput);
                return;
            }

            if (parentDialog != null)
            {
                parentDialog.ExtractItemPublic(item, amount);
            }

            Close();
        }

        private string GetItemLabel()
        {
            if (item.def == null)
            {
                return "未知物品";
            }

            string label = item.def.label;
            if (item.stuffDef != null)
            {
                label = item.stuffDef.label + label;
            }

            return label.CapitalizeFirst();
        }
    }
}

