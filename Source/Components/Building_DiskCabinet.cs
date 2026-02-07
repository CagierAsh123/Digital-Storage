using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DigitalStorage.Services;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    [StaticConstructorOnStartup]
    public class Building_DiskCabinet : Building_Storage
    {
        private static readonly Texture2D LinkTex = ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", true);
        public const int MaxDiskCount = 8;
        // 磁盘容量：单位改为"组"而不是"个"
        private static readonly Dictionary<string, int> DiskCapacities = new Dictionary<string, int>
        {
            { "DigitalStorage_DiskSmall", 50 },    // 50 组
            { "DigitalStorage_DiskMedium", 100 },   // 100 组
            { "DigitalStorage_DiskLarge", 200 }     // 200 组
        };
        private Building_StorageCore boundCore;

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            DigitalStorageMapComponent component = map.GetComponent<DigitalStorageMapComponent>();
            if (component != null)
            {
                component.RegisterCabinet(this);
            }
            if (this.boundCore == null && !respawningAfterLoad)
            {
                this.TryAutoConnect();
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            Map map = base.Map;
            if (map != null)
            {
                DigitalStorageMapComponent component = map.GetComponent<DigitalStorageMapComponent>();
                if (component != null)
                {
                    component.DeregisterCabinet(this);
                }
            }
            this.SetBoundCore(null);
            this.DropAllDisks();
            base.DeSpawn(mode);
        }

        public override void PostMake()
        {
            base.PostMake();
            this.boundCore = null;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look<Building_StorageCore>(ref this.boundCore, "boundCore", false);
        }

        public override void Notify_ReceivedThing(Thing newItem)
        {
            base.Notify_ReceivedThing(newItem);
            if (this.GetDiskCount() > 8)
            {
                List<Thing> disks = this.GetDisks().ToList();
                while (disks.Count > 8)
                {
                    Thing disk = disks[disks.Count - 1];
                    disks.Remove(disk);
                    disk.DeSpawn(DestroyMode.Vanish);
                    GenPlace.TryPlaceThing(disk, base.Position, base.Map, ThingPlaceMode.Near, null, null, null, 1);
                }
            }
        }

        public override string GetInspectString()
        {
            StringBuilder sb = new StringBuilder();
            string baseStr = base.GetInspectString();
            if (!string.IsNullOrEmpty(baseStr))
            {
                sb.AppendLine(baseStr);
            }
            sb.AppendLine("DS_InspectDisks".Translate(this.GetDiskCount(), 8));
            sb.AppendLine("DS_InspectProvidedCapacity".Translate(this.GetProvidedCapacity()));
            if (this.boundCore != null)
            {
                sb.AppendLine("DS_ConnectedTo".Translate(this.boundCore.NetworkName));
            }
            else
            {
                sb.AppendLine("DS_NotConnected".Translate());
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
                defaultLabel = "DS_ConnectToCore".Translate(),
                defaultDesc = "DS_ConnectToCoreDesc".Translate(),
                icon = LinkTex,
                action = delegate()
                {
                    List<FloatMenuOption> options = new List<FloatMenuOption>();
                    
                    // 获取全局游戏组件以支持跨地图
                    Game game = Current.Game;
                    DigitalStorageGameComponent gameComp = (game != null) ? game.GetComponent<DigitalStorageGameComponent>() : null;
                    
                    if (gameComp != null)
                    {
                        // 使用全局组件获取所有地图的核心
                        foreach (Building_StorageCore core in gameComp.GetAllCores())
                        {
                            if (core != null && core.Spawned)
                            {
                                string label = core.NetworkName;
                                // 如果核心在其他地图，显示地图信息
                                if (core.Map != base.Map)
                                {
                                    label = "DS_CoreOnMap".Translate(core.NetworkName, core.Map?.Index ?? -1);
                                }
                                
                                options.Add(new FloatMenuOption(label, delegate()
                                {
                                    this.SetBoundCore(core);
                                }));
                            }
                        }
                    }
                    else
                    {
                        // 回退到只显示当前地图的核心
                        foreach (Building_StorageCore core in base.Map.listerBuildings.AllBuildingsColonistOfClass<Building_StorageCore>())
                        {
                            options.Add(new FloatMenuOption(core.NetworkName, delegate()
                            {
                                this.SetBoundCore(core);
                            }));
                        }
                    }
                    
                    if (options.Count == 0)
                    {
                        options.Add(new FloatMenuOption("DS_NoCoresAvailable".Translate(), null));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };
            yield break;
        }

        public void SetBoundCore(Building_StorageCore core)
        {
            if (this.boundCore != null)
            {
                this.boundCore.DeregisterDiskCabinet(this);
            }
            this.boundCore = core;
            if (this.boundCore != null)
            {
                this.boundCore.RegisterDiskCabinet(this);
            }
        }

        public Building_StorageCore GetBoundCore()
        {
            return this.boundCore;
        }

        public int GetDiskCount()
        {
            return this.GetDisks().Count();
        }

        public IEnumerable<Thing> GetDisks()
        {
            SlotGroup slotGroup = base.GetSlotGroup();
            return slotGroup != null ? slotGroup.HeldThings.Where(IsDisk) : Enumerable.Empty<Thing>();
        }

        public int GetProvidedCapacity()
        {
            int total = 0;
            foreach (Thing disk in this.GetDisks())
            {
                int capacity;
                if (DiskCapacities.TryGetValue(disk.def.defName, out capacity))
                {
                    total += capacity;
                }
            }
            return total;
        }

        private static bool IsDisk(Thing thing)
        {
            return thing != null && DiskCapacities.ContainsKey(thing.def.defName);
        }

        private void TryAutoConnect()
        {
            foreach (IntVec3 cell in GenAdj.CellsAdjacent8Way(this))
            {
                if (!cell.InBounds(base.Map))
                    continue;
                    
                Building building = cell.GetFirstBuilding(base.Map);
                Building_StorageCore core = building as Building_StorageCore;
                if (core != null)
                {
                    this.SetBoundCore(core);
                    break;
                }
            }
        }

        private void DropAllDisks()
        {
            foreach (Thing disk in this.GetDisks().ToList())
            {
                if (disk != null && !disk.Destroyed)
                {
                    disk.DeSpawn(DestroyMode.Vanish);
                    GenPlace.TryPlaceThing(disk, base.Position, base.Map, ThingPlaceMode.Near, null, null, null, 1);
                }
            }
        }
    }
}

