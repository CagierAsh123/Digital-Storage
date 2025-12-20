using UnityEngine;
using Verse;

namespace DigitalStorage.Settings
{
    public class DigitalStorageMod : Mod
    {
        private DigitalStorageSettings settings;

        public DigitalStorageMod(ModContentPack content) : base(content)
        {
            this.settings = GetSettings<DigitalStorageSettings>();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            DigitalStorageSettings.DoSettingsWindowContents(inRect);
            base.DoSettingsWindowContents(inRect);
        }

        public override string SettingsCategory()
        {
            return "数字存储 Digital Storage 2.0";
        }
    }
}

