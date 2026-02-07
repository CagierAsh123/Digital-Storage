using System;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    public class Dialog_RenameNetwork : Window
    {
        public Dialog_RenameNetwork(Building_StorageCore core)
            : base(null)
        {
            this.core = core;
            this.curName = core.NetworkName;
            this.forcePause = true;
            this.doCloseX = true;
            this.absorbInputAroundWindow = true;
            this.closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize
        {
            get
            {
                return new Vector2(280f, 175f);
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 30f), "DS_EnterNetworkName".Translate());
            this.curName = Widgets.TextField(new Rect(0f, 35f, inRect.width, 35f), this.curName);
            bool flag = Widgets.ButtonText(new Rect(0f, inRect.height - 35f, inRect.width / 2f - 5f, 35f), "DS_Confirm".Translate(), true, true, true, null);
            if (flag)
            {
                bool flag2 = !string.IsNullOrEmpty(this.curName);
                if (flag2)
                {
                    this.core.NetworkName = this.curName;
                }
                this.Close(true);
            }
            bool flag3 = Widgets.ButtonText(new Rect(inRect.width / 2f + 5f, inRect.height - 35f, inRect.width / 2f - 5f, 35f), "DS_Cancel".Translate(), true, true, true, null);
            if (flag3)
            {
                this.Close(true);
            }
        }

        private Building_StorageCore core;

        private string curName;
    }
}

