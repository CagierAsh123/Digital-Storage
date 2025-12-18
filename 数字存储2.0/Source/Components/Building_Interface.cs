using System;
using System.Collections.Generic;
using System.Text;
using DigitalStorage.Services;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    [StaticConstructorOnStartup]
    public class Building_Interface : Building_Storage
    {
        private static readonly Texture2D LinkTex;
        private Building_StorageCore boundCore;
        private CompPowerTrader powerComp;

        static Building_Interface()
        {
            Texture2D texture2D;
            if ((texture2D = ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", false)) == null)
            {
                texture2D = ContentFinder<Texture2D>.Get("UI/Buttons/Dev/Add", false) ?? BaseContent.BadTex;
            }
            LinkTex = texture2D;
        }

        public Building_StorageCore BoundCore
        {
            get { return this.boundCore; }
        }

        public bool Powered
        {
            get
            {
                CompPowerTrader compPowerTrader = this.powerComp;
                return compPowerTrader != null && compPowerTrader.PowerOn;
            }
        }

        public bool IsActive
        {
            get { return this.Powered && this.boundCore != null && this.boundCore.Powered; }
        }

        public override void PostMake()
        {
            base.PostMake();
            this.boundCore = null;
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            this.powerComp = base.GetComp<CompPowerTrader>();
            DigitalStorageMapComponent component = map.GetComponent<DigitalStorageMapComponent>();
            if (component != null)
            {
                component.RegisterInterface(this);
            }
            this.RefreshStoreSettings();
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
                    component.DeregisterInterface(this);
                }
            }
            this.SetBoundCore(null);
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look<Building_StorageCore>(ref this.boundCore, "boundCore", false);
        }

        public override string GetInspectString()
        {
            StringBuilder sb = new StringBuilder();
            string baseStr = base.GetInspectString();
            if (!string.IsNullOrEmpty(baseStr))
            {
                sb.AppendLine(baseStr);
            }
            if (this.boundCore != null)
            {
                sb.AppendLine("连接到: " + this.boundCore.NetworkName);
                sb.AppendLine(string.Format("存储: {0}/{1}", this.boundCore.GetUsedCapacity(), this.boundCore.GetCapacity()));
            }
            else
            {
                sb.AppendLine("未连接");
            }
            if (!this.Powered)
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
                defaultDesc = "选择要连接的存储核心（支持跨地图）",
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
                                    label = string.Format("{0} (地图 {1})", core.NetworkName, core.Map?.Index ?? -1);
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
                        options.Add(new FloatMenuOption("无可用核心", null));
                    }
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };
            yield break;
        }

        public void SetBoundCore(Building_StorageCore core)
        {
            Building_StorageCore oldCore = this.boundCore;
            if (this.boundCore != null)
            {
                this.boundCore.DeregisterInterface(this);
            }
            this.boundCore = core;
            if (this.boundCore != null)
            {
                this.boundCore.RegisterInterface(this);
            }
            Map map = base.Map;
            if (map != null)
            {
                DigitalStorageMapComponent component = map.GetComponent<DigitalStorageMapComponent>();
                if (component != null)
                {
                    component.UpdateInterfaceBinding(this, oldCore, core);
                }
            }
            this.RefreshStoreSettings();
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

        private void RefreshStoreSettings()
        {
            if (this.boundCore != null)
            {
                this.settings = this.boundCore.GetStoreSettings();
            }
        }

        public override void Notify_ReceivedThing(Thing newItem)
        {
            base.Notify_ReceivedThing(newItem);
            
            // 如果接口未激活或核心不可用，不处理物品
            if (!this.IsActive || this.boundCore == null)
            {
                return;
            }
            
            // 检查核心是否可以接收物品
            if (this.boundCore.CanReceiveThing(newItem))
            {
                if (newItem.Spawned)
                {
                    newItem.DeSpawn(DestroyMode.Vanish);
                }
                Map targetMap = this.boundCore.Map ?? base.Map;
                GenPlace.TryPlaceThing(newItem, this.boundCore.Position, targetMap, ThingPlaceMode.Near, null, null, null, 1);
                FleckMaker.ThrowLightningGlow(this.boundCore.DrawPos, targetMap, 0.5f);
            }
        }

        public new bool Accepts(Thing t)
        {
            // 如果接口未激活或核心不可用，拒绝接受物品
            if (!this.IsActive)
            {
                return false;
            }
            
            // 如果核心不存在或无法接收，拒绝
            if (this.boundCore == null || !this.boundCore.CanReceiveThing(t))
            {
                return false;
            }
            
            return base.Accepts(t);
        }

        public new StorageSettings GetStoreSettings()
        {
            if (this.boundCore != null)
            {
                return this.boundCore.GetStoreSettings();
            }
            return this.settings;
        }

        public new StorageSettings GetParentStoreSettings()
        {
            if (this.boundCore != null)
            {
                return this.boundCore.GetParentStoreSettings();
            }
            BuildingProperties building = this.def.building;
            return (building != null ? building.fixedStorageSettings : null) ?? StorageSettings.EverStorableFixedSettings();
        }
    }
}

