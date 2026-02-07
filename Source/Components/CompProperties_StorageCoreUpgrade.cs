using System.Collections.Generic;
using Verse;

namespace DigitalStorage.Components
{
    public class CompProperties_StorageCoreUpgrade : CompProperties
    {
        public List<StorageCoreUpgrade> upgrades;

        public CompProperties_StorageCoreUpgrade()
        {
            this.compClass = typeof(CompStorageCoreUpgrade);
        }
    }
}
