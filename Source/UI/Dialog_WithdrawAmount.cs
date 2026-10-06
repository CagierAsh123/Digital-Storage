// =====================================================================================
//  【本地新增文件／整块替换】Dialog_WithdrawAmount —— 取物数量窗（AE2 材质版）
// -------------------------------------------------------------------------------------
//  逻辑与构造参数完全不变（ITab 与右键菜单都用它）：只负责"问个数量 + 回调"。
//  只把外观换成 AE2：WindowFrame 外框 + AE2 文字色 + 凹槽里的数值输入框 + 两颗 AE2 按钮。
// =====================================================================================
using System;
using UnityEngine;
using Verse;

namespace DigitalStorage.UI
{
    public class Dialog_WithdrawAmount : Window
    {
        private readonly string title;
        private readonly int available;
        private readonly int maxCarry;
        private readonly int upperBound;
        private readonly Action<int> onConfirm;
        private string amountStr;
        private int amount;

        public override Vector2 InitialSize { get { return new Vector2(360f, 220f); } }

        public Dialog_WithdrawAmount(string title, int available, int maxCarry, Action<int> onConfirm)
        {
            this.title = title;
            this.available = Math.Max(available, 0);
            this.maxCarry = maxCarry;
            this.onConfirm = onConfirm;
            this.upperBound = (maxCarry > 0) ? Math.Min(this.available, maxCarry) : this.available;
            if (this.upperBound <= 0) this.upperBound = 1;

            this.amount = this.upperBound;
            this.amountStr = this.amount.ToString();
            this.forcePause = true;
            this.doCloseX = false;
            this.absorbInputAroundWindow = false;   // ★ 让环世界继续处理全局按键（空格/加速/摄像机）
            this.closeOnClickedOutside = true;
            this.doWindowBackground = false;   // ★ AE2：全自绘
        }

        public override void DoWindowContents(Rect inRect)
        {
            try
            {
            bool closeClicked;
            Rect ri = AE2Draw.WindowFrame(inRect.ContractedBy(2f), "DS_WithdrawTitle".Translate(title).ToString(), out closeClicked);
            if (closeClicked) { Close(); return; }

            AE2Draw.HandlePauseHotkey();   // ★ 用户要求：界面开着时空格也能暂停/继续
            float y = ri.y;
            AE2Draw.Tiny(new Rect(ri.x + 2f, y, ri.width - 4f, 18f),
                "DS_WithdrawAvailable".Translate(available).ToString(), AE2Draw.TextCol);
            y += 20f;
            if (maxCarry > 0)
            {
                AE2Draw.Tiny(new Rect(ri.x + 2f, y, ri.width - 4f, 18f),
                    "DS_WithdrawCarryLimit".Translate(maxCarry).ToString(), AE2Draw.TextDimCol);
                y += 20f;
            }
            y += 6f;

            // 数值输入框：AE2 凹槽 + 原版的 TextFieldNumeric（保留 1..upperBound 的夹取逻辑）
            Rect box = new Rect(ri.x, y, ri.width, 30f);
            AE2Draw.Sunken(box, AE2Draw.Slot);
            Widgets.TextFieldNumeric(box.ContractedBy(4f), ref amount, ref amountStr, 1, upperBound);
            y += 38f;

            float btnW = ri.width / 2f - 6f;
            Rect okR = new Rect(ri.x, y, btnW, 28f);
            Rect cancelR = new Rect(ri.x + btnW + 12f, y, btnW, 28f);
            if (AE2Draw.TextButton(okR, "DS_Confirm".Translate().ToString(), true))
            {
                if (amount > 0 && onConfirm != null) onConfirm(amount);
                Close();
            }
            if (AE2Draw.TextButton(cancelR, "DS_Cancel".Translate().ToString()))
            {
                Close();
            }
            }
            catch (Exception __uiEx) { Log.ErrorOnce("[DigitalStorage] AE2 界面绘制异常（只记一次，界面不会卡死）：" + __uiEx, 771003); }
        }
    }
}
