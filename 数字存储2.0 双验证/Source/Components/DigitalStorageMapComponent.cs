using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DigitalStorage.Components
{
    public class DigitalStorageMapComponent : MapComponent
    {
        private List<Building_StorageCore> cores = new List<Building_StorageCore>();
        private HashSet<IntVec3> cabinetPositions = new HashSet<IntVec3>();

        public DigitalStorageMapComponent(Map map)
            : base(map)
        {
        }

        public void RegisterCore(Building_StorageCore core)
        {
            if (core != null && !this.cores.Contains(core))
            {
                this.cores.Add(core);
            }
        }

        public void DeregisterCore(Building_StorageCore core)
        {
            if (core != null)
            {
                this.cores.Remove(core);
            }
        }

        public List<Building_StorageCore> GetAllCores()
        {
            return this.cores;
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
    }
}
