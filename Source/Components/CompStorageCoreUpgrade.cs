using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 存储核心升级组件
    /// 通过 Gizmo 按钮升级/降级，每个等级有不同的容量和耗电
    /// </summary>
    public class CompStorageCoreUpgrade : ThingComp
    {
        private int level = 0;
        private int upgradeProgressTick = -1;
        private int downgradeProgressTick = -1;
        private int upgradeDirection = 0; // 1=升级中, -1=降级中, 0=空闲
        private int cooldownUntilTick = -1;

        public CompProperties_StorageCoreUpgrade Props => (CompProperties_StorageCoreUpgrade)this.props;

        public int Level => level;
        public int MinLevel => 0;
        public int MaxLevel => Props.upgrades.Count - 1;
        public StorageCoreUpgrade CurrentUpgrade => Props.upgrades[level];
        public bool IsUpgrading => upgradeDirection > 0;
        public bool IsDowngrading => upgradeDirection < 0;
        public bool IsBusy => upgradeDirection != 0;
        public bool IsOnCooldown => cooldownUntilTick > Find.TickManager.TicksGame;

        /// <summary>
        /// 当前等级的容量
        /// </summary>
        public int GetCapacity()
        {
            return CurrentUpgrade.capacity;
        }

        /// <summary>
        /// 当前等级的耗电量
        /// </summary>
        public float GetPowerConsumption()
        {
            return CurrentUpgrade.powerConsumption;
        }

        public override void CompTick()
        {
            base.CompTick();

            if (upgradeDirection == 0) return;

            // 检查电力
            CompPowerTrader power = this.parent.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn)
            {
                return; // 没电时暂停升级
            }

            if (upgradeDirection > 0)
            {
                upgradeProgressTick++;
                StorageCoreUpgrade nextUpgrade = Props.upgrades[level + 1];
                if (upgradeProgressTick >= nextUpgrade.upgradeDurationTicks)
                {
                    CompleteUpgrade();
                }
            }
            else if (upgradeDirection < 0)
            {
                downgradeProgressTick++;
                StorageCoreUpgrade currentUpgrade = Props.upgrades[level];
                if (downgradeProgressTick >= currentUpgrade.upgradeDurationTicks / 2) // 降级只需一半时间
                {
                    CompleteUpgrade();
                }
            }
        }

        private void StartUpgrade()
        {
            if (level >= MaxLevel || IsBusy || IsOnCooldown) return;

            StorageCoreUpgrade nextUpgrade = Props.upgrades[level + 1];

            // 检查并消耗材料
            if (nextUpgrade.upgradeCost != null && nextUpgrade.upgradeCost.Count > 0)
            {
                if (!HasRequiredMaterials(nextUpgrade.upgradeCost))
                {
                    Messages.Message("DS_UpgradeNoMaterials".Translate(), this.parent, MessageTypeDefOf.RejectInput);
                    return;
                }
                ConsumeMaterials(nextUpgrade.upgradeCost);
            }

            upgradeDirection = 1;
            upgradeProgressTick = 0;
            Messages.Message("DS_UpgradeStarted".Translate(this.parent.LabelCap), this.parent, MessageTypeDefOf.NeutralEvent);
        }

        private void StartDowngrade()
        {
            if (level <= MinLevel || IsBusy || IsOnCooldown) return;

            // 检查降级后容量是否足够
            Building_StorageCore core = this.parent as Building_StorageCore;
            if (core != null)
            {
                StorageCoreUpgrade prevUpgrade = Props.upgrades[level - 1];
                int usedCapacity = core.GetUsedCapacity();
                if (usedCapacity > prevUpgrade.capacity)
                {
                    Messages.Message("DS_DowngradeTooFull".Translate(usedCapacity, prevUpgrade.capacity), this.parent, MessageTypeDefOf.RejectInput);
                    return;
                }
            }

            upgradeDirection = -1;
            downgradeProgressTick = 0;
            Messages.Message("DS_DowngradeStarted".Translate(this.parent.LabelCap), this.parent, MessageTypeDefOf.NeutralEvent);
        }

        private void CompleteUpgrade()
        {
            level += upgradeDirection;
            level = Mathf.Clamp(level, MinLevel, MaxLevel);

            // 更新耗电
            CompPowerTrader power = this.parent.TryGetComp<CompPowerTrader>();
            if (power != null)
            {
                power.PowerOutput = -CurrentUpgrade.powerConsumption;
            }

            // 设置冷却
            cooldownUntilTick = Find.TickManager.TicksGame + CurrentUpgrade.cooldownTicks;

            string msgKey = upgradeDirection > 0 ? "DS_UpgradeComplete" : "DS_DowngradeComplete";
            Messages.Message(msgKey.Translate(this.parent.LabelCap, CurrentUpgrade.GetLabel()), this.parent, MessageTypeDefOf.PositiveEvent);

            upgradeDirection = 0;
            upgradeProgressTick = -1;
            downgradeProgressTick = -1;
        }

        private bool HasRequiredMaterials(List<UpgradeCostEntry> costs)
        {
            if (this.parent.Map == null) return false;

            foreach (UpgradeCostEntry cost in costs)
            {
                if (cost == null || cost.thingDef == null) continue;

                int available = this.parent.Map.resourceCounter.GetCount(cost.thingDef);

                Building_StorageCore core = this.parent as Building_StorageCore;
                if (core != null)
                {
                    available += core.GetVirtualItemCount(cost.thingDef);
                }

                if (available < cost.count)
                {
                    return false;
                }
            }
            return true;
        }

        private void ConsumeMaterials(List<UpgradeCostEntry> costs)
        {
            if (this.parent.Map == null) return;

            foreach (UpgradeCostEntry cost in costs)
            {
                if (cost == null || cost.thingDef == null) continue;

                int remaining = cost.count;

                // 先从地图上消耗
                List<Thing> mapThings = this.parent.Map.listerThings.ThingsOfDef(cost.thingDef);
                foreach (Thing thing in mapThings.ToList())
                {
                    if (remaining <= 0) break;
                    if (!thing.Spawned) continue;

                    int take = Math.Min(thing.stackCount, remaining);
                    remaining -= take;

                    if (take >= thing.stackCount)
                    {
                        thing.Destroy(DestroyMode.Vanish);
                    }
                    else
                    {
                        thing.SplitOff(take).Destroy(DestroyMode.Vanish);
                    }
                }

                // 不够的从虚拟存储扣
                if (remaining > 0)
                {
                    Building_StorageCore core = this.parent as Building_StorageCore;
                    if (core != null)
                    {
                        core.DeductVirtualItems(cost.thingDef, remaining);
                    }
                }
            }
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }

            // 升级按钮
            Command_Action upgradeCmd = new Command_Action
            {
                defaultLabel = "DS_Upgrade".Translate(),
                defaultDesc = GetUpgradeDesc(),
                icon = ContentFinder<Texture2D>.Get("up", false) ?? ContentFinder<Texture2D>.Get("UI/Designators/Open", true),
                action = delegate { StartUpgrade(); }
            };

            // 降级按钮
            Command_Action downgradeCmd = new Command_Action
            {
                defaultLabel = "DS_Downgrade".Translate(),
                defaultDesc = GetDowngradeDesc(),
                icon = ContentFinder<Texture2D>.Get("down", false) ?? ContentFinder<Texture2D>.Get("UI/Designators/Cancel", true),
                action = delegate { StartDowngrade(); }
            };

            // 禁用条件
            CompPowerTrader power = this.parent.TryGetComp<CompPowerTrader>();
            if (power != null && !power.PowerOn)
            {
                upgradeCmd.Disable("DS_NoPower".Translate());
                downgradeCmd.Disable("DS_NoPower".Translate());
            }
            else if (IsBusy)
            {
                string busyKey = IsUpgrading ? "DS_UpgradeInProgress" : "DS_DowngradeInProgress";
                float progress = GetProgress();
                upgradeCmd.Disable(busyKey.Translate(progress.ToStringPercent()));
                downgradeCmd.Disable(busyKey.Translate(progress.ToStringPercent()));
            }
            else if (IsOnCooldown)
            {
                string cooldownStr = GenDate.ToStringTicksToPeriod(cooldownUntilTick - Find.TickManager.TicksGame);
                upgradeCmd.Disable("DS_UpgradeCooldown".Translate(cooldownStr));
                downgradeCmd.Disable("DS_UpgradeCooldown".Translate(cooldownStr));
            }

            if (level >= MaxLevel)
            {
                upgradeCmd.Disable("DS_MaxLevel".Translate());
            }

            if (level <= MinLevel)
            {
                downgradeCmd.Disable("DS_MinLevel".Translate());
            }

            yield return upgradeCmd;
            yield return downgradeCmd;
        }

        private float GetProgress()
        {
            if (IsUpgrading && level < MaxLevel)
            {
                StorageCoreUpgrade nextUpgrade = Props.upgrades[level + 1];
                return (float)upgradeProgressTick / nextUpgrade.upgradeDurationTicks;
            }
            if (IsDowngrading)
            {
                StorageCoreUpgrade currentUpgrade = Props.upgrades[level];
                return (float)downgradeProgressTick / (currentUpgrade.upgradeDurationTicks / 2f);
            }
            return 0f;
        }

        private string GetUpgradeDesc()
        {
            if (level >= MaxLevel)
            {
                return "DS_MaxLevelDesc".Translate();
            }

            StorageCoreUpgrade next = Props.upgrades[level + 1];
            string desc = "DS_UpgradeDesc".Translate(
                CurrentUpgrade.capacity,
                next.capacity,
                CurrentUpgrade.powerConsumption,
                next.powerConsumption);

            if (next.upgradeCost != null && next.upgradeCost.Count > 0)
            {
                desc += "\n\n" + "DS_UpgradeCost".Translate(next.GetUpgradeCostString());
            }

            return desc;
        }

        private string GetDowngradeDesc()
        {
            if (level <= MinLevel)
            {
                return "DS_MinLevelDesc".Translate();
            }

            StorageCoreUpgrade prev = Props.upgrades[level - 1];
            return "DS_DowngradeDesc".Translate(
                CurrentUpgrade.capacity,
                prev.capacity,
                CurrentUpgrade.powerConsumption,
                prev.powerConsumption);
        }

        public override string CompInspectStringExtra()
        {
            string result = "DS_InspectLevel".Translate(level + 1, Props.upgrades.Count);

            if (IsBusy)
            {
                float progress = GetProgress();
                string busyKey = IsUpgrading ? "DS_UpgradeInProgress" : "DS_DowngradeInProgress";
                result += "\n" + busyKey.Translate(progress.ToStringPercent());
            }

            return result;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref level, "upgradeLevel", 0);
            Scribe_Values.Look(ref upgradeDirection, "upgradeDirection", 0);
            Scribe_Values.Look(ref upgradeProgressTick, "upgradeProgressTick", -1);
            Scribe_Values.Look(ref downgradeProgressTick, "downgradeProgressTick", -1);
            Scribe_Values.Look(ref cooldownUntilTick, "cooldownUntilTick", -1);

            // 安全检查
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                level = Mathf.Clamp(level, 0, MaxLevel);
            }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);

            // 初始化耗电
            CompPowerTrader power = this.parent.TryGetComp<CompPowerTrader>();
            if (power != null)
            {
                power.PowerOutput = -CurrentUpgrade.powerConsumption;
            }
        }
    }
}
