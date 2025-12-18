using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DigitalStorage.Components
{
    public class DigitalStorageMapComponent : MapComponent
    {
        public DigitalStorageMapComponent(Map map)
            : base(map)
        {
        }

        public void RegisterCore(Building_StorageCore core)
        {
            if (core != null && !this.cores.Contains(core))
            {
                this.cores.Add(core);
                this.coreToInterfaces[core] = new List<Building_Interface>();
            }
        }

        public void DeregisterCore(Building_StorageCore core)
        {
            if (core != null)
            {
                this.cores.Remove(core);
                this.coreToInterfaces.Remove(core);
            }
        }

        public List<Building_StorageCore> GetAllCores()
        {
            return this.cores;
        }

        public void RegisterInterface(Building_Interface port)
        {
            if (port != null && !this.interfaces.Contains(port))
            {
                this.interfaces.Add(port);
                List<Building_Interface> list = null;
                if (port.BoundCore != null && this.coreToInterfaces.TryGetValue(port.BoundCore, out list))
                {
                    if (!list.Contains(port))
                    {
                        list.Add(port);
                    }
                }
            }
        }

        public void DeregisterInterface(Building_Interface port)
        {
            if (port != null)
            {
                this.interfaces.Remove(port);
                foreach (List<Building_Interface> list in this.coreToInterfaces.Values)
                {
                    list.Remove(port);
                }
            }
        }

        public void UpdateInterfaceBinding(Building_Interface port, Building_StorageCore oldCore, Building_StorageCore newCore)
        {
            if (oldCore != null && this.coreToInterfaces.TryGetValue(oldCore, out List<Building_Interface> oldList))
            {
                oldList.Remove(port);
            }
            if (newCore != null && this.coreToInterfaces.TryGetValue(newCore, out List<Building_Interface> newList))
            {
                if (!newList.Contains(port))
                {
                    newList.Add(port);
                }
            }
        }

        public List<Building_Interface> GetAllInterfaces()
        {
            return this.interfaces;
        }

        public void RegisterOutputInterface(Building_OutputInterface port)
        {
            if (port != null && !this.outputInterfaces.Contains(port))
            {
                this.outputInterfaces.Add(port);
            }
        }

        public void DeregisterOutputInterface(Building_OutputInterface port)
        {
            if (port != null)
            {
                this.outputInterfaces.Remove(port);
            }
        }

        public List<Building_OutputInterface> GetAllOutputInterfaces()
        {
            return this.outputInterfaces;
        }

        public List<Building_Interface> GetInterfacesForCore(Building_StorageCore core)
        {
            if (core != null && this.coreToInterfaces.TryGetValue(core, out List<Building_Interface> list))
            {
                return list;
            }
            return null;
        }

        public bool IsPawnNearAnyInterface(IntVec3 pawnPos)
        {
            for (int i = 0; i < this.interfaces.Count; i++)
            {
                Building_Interface iface = this.interfaces[i];
                if (iface != null && iface.Spawned && iface.IsActive)
                {
                    if ((float)IntVec3Utility.DistanceToSquared(pawnPos, iface.Position) <= 6.25f)
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public void RegisterCabinet(Building_DiskCabinet cabinet)
        {
            if (cabinet != null)
            {
                this.cabinetPositions.Add(cabinet.Position);
            }
        }

        public void DeregisterCabinet(Building_DiskCabinet cabinet)
        {
            if (cabinet != null)
            {
                this.cabinetPositions.Remove(cabinet.Position);
            }
        }

        public bool IsCabinetPosition(IntVec3 pos)
        {
            return this.cabinetPositions.Contains(pos);
        }

        /// <summary>
        /// 查找包含指定物品的核心（支持预留物品系统）
        /// </summary>
        public Building_StorageCore FindCoreWithItem(Thing thing)
        {
            if (thing == null || !thing.Spawned)
            {
                return null;
            }

            for (int i = 0; i < this.cores.Count; i++)
            {
                Building_StorageCore core = this.cores[i];
                if (core == null || !core.Spawned || !core.Powered)
                {
                    continue;
                }

                SlotGroup slotGroup = core.GetSlotGroup();
                if (slotGroup != null)
                {
                    // 通过引用匹配（预留物品）
                    foreach (Thing t in slotGroup.HeldThings)
                    {
                        if (t == thing)
                        {
                            return core;
                        }
                    }
                }

                // 通过位置匹配
                if (slotGroup != null && slotGroup.CellsList != null)
                {
                    foreach (IntVec3 cell in slotGroup.CellsList)
                    {
                        if (cell == thing.Position)
                        {
                            List<Thing> thingsAtCell = thing.Map.thingGrid.ThingsListAt(cell);
                            for (int j = 0; j < thingsAtCell.Count; j++)
                            {
                                Thing t2 = thingsAtCell[j];
                                if (t2 == thing || (t2.def == thing.def && t2.Stuff == thing.Stuff))
                                {
                                    return core;
                                }
                            }
                        }
                    }
                }

                // 检查虚拟存储（如果预留物品中没有）
                if (core.HasItem(thing.def, thing.Stuff))
                {
                    return core;
                }
            }

            return null;
        }

        public bool IsCorePosition(IntVec3 pos)
        {
            for (int i = 0; i < this.cores.Count; i++)
            {
                Building_StorageCore building_StorageCore = this.cores[i];
                if (building_StorageCore != null && building_StorageCore.Position == pos)
                {
                    return true;
                }
            }
            return false;
        }

        private List<Building_StorageCore> cores = new List<Building_StorageCore>();
        private List<Building_Interface> interfaces = new List<Building_Interface>();
        private List<Building_OutputInterface> outputInterfaces = new List<Building_OutputInterface>();
        private HashSet<IntVec3> cabinetPositions = new HashSet<IntVec3>();
        private Dictionary<Building_StorageCore, List<Building_Interface>> coreToInterfaces = new Dictionary<Building_StorageCore, List<Building_Interface>>();
    }
}

