using System.Collections.Generic;
using System.Text;
using DigitalStorage.Services;
using DigitalStorage.Settings;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 输入接口：pawn 把物品放到接口上，物品自动传送到绑定的核心
    /// </summary>
    public class Building_InputInterface : Building_Storage
    {
        private Building_StorageCore boundCore;
        private CompPowerTrader powerComp;

        public Building_StorageCore BoundCore => boundCore;

        public bool Powered => powerComp != null && powerComp.PowerOn;

        public bool IsActive => Powered && boundCore != null && boundCore.Powered;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            powerComp = GetComp<CompPowerTrader>();
            
            // 只在新建时自动连接，加载存档时不自动连接
            if (!respawningAfterLoad && boundCore == null && Map != null)
            {
                TryAutoConnect();
            }
            RefreshStoreSettings();
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            SetBoundCore(null);
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref boundCore, "boundCore");
        }

        public override string GetInspectString()
        {
            StringBuilder sb = new StringBuilder();
            string baseStr = base.GetInspectString();
            if (!string.IsNullOrEmpty(baseStr))
            {
                sb.AppendLine(baseStr);
            }
            
            if (boundCore != null)
            {
                sb.AppendLine("连接到: " + boundCore.NetworkName);
                sb.AppendLine($"存储: {boundCore.GetUsedCapacity()}/{boundCore.GetCapacity()}");
            }
            else
            {
                sb.AppendLine("未连接");
            }
            
            if (!Powered)
            {
                sb.AppendLine("无电力");
            }
            return sb.ToString().TrimEnd();
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }

            yield return new Command_Action
            {
                defaultLabel = "连接到核心",
                defaultDesc = "选择要连接的存储核心",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", true),
                action = delegate
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();

                    if (gameComp != null)
                    {
                        foreach (Building_StorageCore core in gameComp.GetAllCores())
                        {
                            if (core != null && core.Spawned && core.Map == Map)
                            {
                                options.Add(new FloatMenuOption(core.NetworkName, () => SetBoundCore(core)));
                            }
                        }
                    }

                    if (options.Count == 0)
                    {
                        options.Add(new FloatMenuOption("无可用核心", null));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };
        }

        public void SetBoundCore(Building_StorageCore core)
        {
            boundCore = core;
            RefreshStoreSettings();
        }

        private void TryAutoConnect()
        {
            if (Map == null) return;
            
            foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(this))
            {
                if (!cell.InBounds(Map))
                    continue;

                Building_StorageCore core = cell.GetFirstBuilding(Map) as Building_StorageCore;
                if (core != null)
                {
                    SetBoundCore(core);
                    break;
                }
            }
        }

        private void RefreshStoreSettings()
        {
            if (boundCore != null)
            {
                settings = boundCore.GetStoreSettings();
            }
        }

        /// <summary>
        /// 当物品被放到接口上时，自动传送到核心
        /// </summary>
        public override void Notify_ReceivedThing(Thing newItem)
        {
            base.Notify_ReceivedThing(newItem);

            if (!IsActive || boundCore == null || boundCore.Map == null)
            {
                return;
            }

            if (!boundCore.CanReceiveThing(newItem))
            {
                return;
            }

            // 传送物品到核心
            if (newItem.Spawned)
            {
                newItem.DeSpawn(DestroyMode.Vanish);
            }
            
            GenPlace.TryPlaceThing(newItem, boundCore.Position, boundCore.Map, ThingPlaceMode.Near);
            FleckMaker.ThrowLightningGlow(boundCore.DrawPos, boundCore.Map, 0.5f);

            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] 输入接口传送: {newItem.Label} x{newItem.stackCount} 到 {boundCore.NetworkName}");
            }
        }

        public new bool Accepts(Thing t)
        {
            if (!IsActive || boundCore == null)
            {
                return false;
            }

            if (!boundCore.CanReceiveThing(t))
            {
                return false;
            }

            return base.Accepts(t);
        }

        public new StorageSettings GetStoreSettings()
        {
            return boundCore?.GetStoreSettings() ?? settings;
        }

        public new StorageSettings GetParentStoreSettings()
        {
            if (boundCore != null)
            {
                return boundCore.GetParentStoreSettings();
            }
            return def.building?.fixedStorageSettings ?? StorageSettings.EverStorableFixedSettings();
        }
    }
}
