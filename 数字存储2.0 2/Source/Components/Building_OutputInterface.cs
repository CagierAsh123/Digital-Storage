using System.Collections.Generic;
using DigitalStorage.Services;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 输出接口：只用于从虚拟存储提取物品，不能放入物品
    /// 新架构：不需要 GhostMarker，预留物品系统确保 WorkGiver 能找到真实物品
    /// </summary>
    [StaticConstructorOnStartup]
    public class Building_OutputInterface : Building
    {
        private static readonly Texture2D LinkTex;
        private Building_StorageCore boundCore;
        private CompPowerTrader powerComp;

        static Building_OutputInterface()
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

        public bool IsActive
        {
            get
            {
                return this.boundCore != null && this.Powered;
            }
        }

        public bool Powered
        {
            get
            {
                CompPowerTrader compPowerTrader = this.powerComp;
                return compPowerTrader != null && compPowerTrader.PowerOn;
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            this.powerComp = base.GetComp<CompPowerTrader>();
            
            // 注册到 MapComponent
            DigitalStorageMapComponent mapComp = map.GetComponent<DigitalStorageMapComponent>();
            if (mapComp != null)
            {
                mapComp.RegisterOutputInterface(this);
            }
            
            if (!respawningAfterLoad)
            {
                TryFindAndBindCore();
            }
        }

        public override void DeSpawn(DestroyMode mode = DestroyMode.Vanish)
        {
            // 从 MapComponent 注销
            Map map = base.Map;
            if (map != null)
            {
                DigitalStorageMapComponent mapComp = map.GetComponent<DigitalStorageMapComponent>();
                if (mapComp != null)
                {
                    mapComp.DeregisterOutputInterface(this);
                }
            }
            
            if (this.boundCore != null)
            {
                this.boundCore.DeregisterOutputInterface(this);
                this.boundCore = null;
            }
            
            base.DeSpawn(mode);
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look<Building_StorageCore>(ref this.boundCore, "boundCore", false);
        }

        public override void TickRare()
        {
            base.TickRare();
            
            if (this.boundCore == null || !this.boundCore.Spawned)
            {
                TryFindAndBindCore();
            }
        }

        public override string GetInspectString()
        {
            string text = base.GetInspectString();
            
            if (this.boundCore != null && this.boundCore.Spawned)
            {
                if (!string.IsNullOrEmpty(text))
                {
                    text += "\n";
                }
                text += "已连接到: " + this.boundCore.NetworkName;
            }
            else
            {
                if (!string.IsNullOrEmpty(text))
                {
                    text += "\n";
                }
                text += "未连接到存储核心";
            }
            
            if (!this.Powered)
            {
                if (!string.IsNullOrEmpty(text))
                {
                    text += "\n";
                }
                text += "无电力";
            }
            
            return text;
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
                                
                                Building_StorageCore coreLocal = core;
                                options.Add(new FloatMenuOption(label, delegate()
                                {
                                    this.SetBoundCore(coreLocal);
                                }, MenuOptionPriority.Default, null, null, 0f, null, null));
                            }
                        }
                    }
                    
                    if (options.Count == 0)
                    {
                        options.Add(new FloatMenuOption("没有可用的存储核心", null, MenuOptionPriority.Default, null, null, 0f, null, null));
                    }
                    
                    Find.WindowStack.Add(new FloatMenu(options));
                }
            };
            
            // 断开连接按钮
            if (this.boundCore != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "断开连接",
                    defaultDesc = "断开与当前核心的连接",
                    icon = ContentFinder<Texture2D>.Get("UI/Designators/Cancel", true),
                    action = delegate()
                    {
                        this.SetBoundCore(null);
                    }
                };
            }
            
            yield break;
        }

        private void TryFindAndBindCore()
        {
            if (this.boundCore != null && this.boundCore.Spawned)
            {
                return;
            }

            DigitalStorageMapComponent component = base.Map.GetComponent<DigitalStorageMapComponent>();
            if (component == null)
            {
                return;
            }

            Building_StorageCore nearestCore = null;
            float nearestDist = float.MaxValue;

            foreach (Building_StorageCore core in component.GetAllCores())
            {
                if (core != null && core.Spawned && core.Powered)
                {
                    float dist = (core.Position - base.Position).LengthHorizontalSquared;
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearestCore = core;
                    }
                }
            }

            if (nearestCore != null)
            {
                SetBoundCore(nearestCore);
            }
        }

        public void SetBoundCore(Building_StorageCore core)
        {
            if (this.boundCore != null)
            {
                this.boundCore.DeregisterOutputInterface(this);
            }

            this.boundCore = core;

            if (core != null)
            {
                core.RegisterOutputInterface(this);
            }
        }
    }
}

