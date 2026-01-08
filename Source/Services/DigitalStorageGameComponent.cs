// TODO: 待部分修改 - 新架构
// 需要优化的部分：
// 1. 优化 FindCoreWithItemType：支持所有地图类型（包括车辆地图、世界地图等）
// 2. 添加 Map.BaseMap() 兼容性检查（Vehicle Map Framework）
// 3. 优化跨地图查找性能（缓存机制）

using System;
using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Components;
using DigitalStorage.Services;
using RimWorld;
using Verse;

namespace DigitalStorage.Services
{
    public class DigitalStorageGameComponent : GameComponent
    {
        public DigitalStorageGameComponent(Game game)
        {
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
        }

        public override void GameComponentUpdate()
        {
            base.GameComponentUpdate();
            
            // 每帧更新异步物品转换器（分帧处理，减少 GC 压力）
            AsyncItemConverter.Update();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look<Building_StorageCore>(ref this.globalCores, "globalCores", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                if (this.globalCores == null)
                {
                    this.globalCores = new List<Building_StorageCore>();
                }
                this.RebuildCache();
            }
        }

        public void RegisterCore(Building_StorageCore core)
        {
            if (core == null || this.globalCores.Contains(core))
            {
                return;
            }

            this.globalCores.Add(core);
            
            if (core.Map != null)
            {
                if (!this.coresByMap.TryGetValue(core.Map, out List<Building_StorageCore> list))
                {
                    list = new List<Building_StorageCore>();
                    this.coresByMap[core.Map] = list;
                }
                
                if (!list.Contains(core))
                {
                    list.Add(core);
                }
            }
        }

        public void DeregisterCore(Building_StorageCore core)
        {
            if (core == null)
            {
                return;
            }

            this.globalCores.Remove(core);
            
            if (core.Map != null && this.coresByMap.TryGetValue(core.Map, out List<Building_StorageCore> list))
            {
                list.Remove(core);
            }
        }

        /// <summary>
        /// 获取所有核心
        /// </summary>
        public List<Building_StorageCore> GetAllCores()
        {
            this.globalCores.RemoveAll((Building_StorageCore c) => c == null || c.Destroyed);
            return this.globalCores;
        }

        /// <summary>
        /// 获取指定地图上的核心（优化：优先本地图查找）
        /// </summary>
        public List<Building_StorageCore> GetCoresOnMap(Map map)
        {
            if (map == null)
            {
                return new List<Building_StorageCore>();
            }

            if (this.coresByMap.TryGetValue(map, out List<Building_StorageCore> list))
            {
                list.RemoveAll((Building_StorageCore c) => c == null || c.Destroyed);
                return list;
            }
            
            return new List<Building_StorageCore>();
        }

        /// <summary>
        /// 重建缓存
        /// </summary>
        private void RebuildCache()
        {
            this.coresByMap.Clear();
            this.globalCores.RemoveAll((Building_StorageCore c) => c == null || c.Destroyed);
            
            foreach (Building_StorageCore core in this.globalCores)
            {
                if (core.Map != null)
                {
                    if (!this.coresByMap.TryGetValue(core.Map, out List<Building_StorageCore> list))
                    {
                        list = new List<Building_StorageCore>();
                        this.coresByMap[core.Map] = list;
                    }
                    list.Add(core);
                }
            }
        }

        /// <summary>
        /// 查找包含指定物品类型的核心（优化：优先本地图查找，支持所有地图类型）
        /// </summary>
        public Building_StorageCore FindCoreWithItemType(ThingDef def, ThingDef stuff = null, Map preferredMap = null)
        {
            // 优先在当前地图查找
            if (preferredMap != null)
            {
                List<Building_StorageCore> mapCores = this.GetCoresOnMap(preferredMap);
                foreach (Building_StorageCore core in mapCores)
                {
                    if (core != null && core.Spawned && core.Powered)
                    {
                        if (core.HasItem(def, stuff))
                        {
                            return core;
                        }
                    }
                }
            }

            // 当前地图没有时，再遍历其他地图（支持所有地图类型）
            Map preferredBaseMap = GetBaseMap(preferredMap);
            HashSet<Map> searchedMaps = new HashSet<Map>();
            if (preferredMap != null)
            {
                searchedMaps.Add(preferredMap);
            }

            foreach (Building_StorageCore core in this.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered || core.Map == null)
                {
                    continue;
                }

                // 跳过已查找的地图（使用 BaseMap 兼容车辆地图）
                Map coreBaseMap = GetBaseMap(core.Map);
                if (preferredBaseMap != null && coreBaseMap == preferredBaseMap)
                {
                    continue;
                }

                // 跳过已搜索的基础地图（避免重复搜索同一基础地图的不同车辆地图）
                if (searchedMaps.Contains(core.Map))
                {
                    continue;
                }

                if (core.HasItem(def, stuff))
                {
                    return core;
                }

                // 标记已搜索的地图
                searchedMaps.Add(core.Map);
            }
            
            return null;
        }

        /// <summary>
        /// 获取地图的基础地图（兼容 Vehicle Map Framework）
        /// </summary>
        private Map GetBaseMap(Map map)
        {
            if (map == null)
            {
                return null;
            }

            // 尝试使用 Vehicle Map Framework 的 BaseMap() 扩展方法
            try
            {
                var baseMapMethod = typeof(Map).GetMethod("BaseMap", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (baseMapMethod != null)
                {
                    return baseMapMethod.Invoke(map, null) as Map ?? map;
                }
            }
            catch
            {
                // Vehicle Map Framework 未安装或方法不存在
            }

            return map;
        }

        /// <summary>
        /// 查找包含指定物品的核心（优化：优先本地图查找）
        /// </summary>
        public Building_StorageCore FindCoreWithItem(Thing item)
        {
            if (item == null)
            {
                return null;
            }

            // 先查物品所在地图
            if (item.Spawned && item.Map != null)
            {
                DigitalStorageMapComponent mapComp = item.Map.GetComponent<DigitalStorageMapComponent>();
                Building_StorageCore core = mapComp?.FindCoreWithItem(item);
                if (core != null)
                {
                    return core;
                }
            }

            // 跨地图查找（优先本地图）
            return this.FindCoreWithItemType(item.def, item.Stuff, item.Map);
        }

        /// <summary>
        /// 从任何核心提取物品（优化：优先本地图查找，支持所有地图类型）
        /// </summary>
        public Thing TryExtractItemFromAnyCoreGlobal(ThingDef def, int count, ThingDef stuff = null, Map preferredMap = null)
        {
            // 优先在当前地图查找
            if (preferredMap != null)
            {
                List<Building_StorageCore> mapCores = this.GetCoresOnMap(preferredMap);
                foreach (Building_StorageCore core in mapCores)
                {
                    if (core != null && core.Spawned && core.Powered)
                    {
                        if (core.HasItem(def, stuff))
                        {
                            Thing extracted = core.ExtractItem(def, count, stuff);
                            if (extracted != null)
                            {
                                return extracted;
                            }
                        }
                    }
                }
            }

            // 当前地图没有时，再遍历其他地图（支持所有地图类型）
            Map preferredBaseMap = GetBaseMap(preferredMap);
            HashSet<Map> searchedMaps = new HashSet<Map>();
            if (preferredMap != null)
            {
                searchedMaps.Add(preferredMap);
            }

            foreach (Building_StorageCore core in this.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered || core.Map == null)
                {
                    continue;
                }

                // 跳过已查找的地图（使用 BaseMap 兼容车辆地图）
                Map coreBaseMap = GetBaseMap(core.Map);
                if (preferredBaseMap != null && coreBaseMap == preferredBaseMap)
                {
                    continue;
                }

                // 跳过已搜索的地图
                if (searchedMaps.Contains(core.Map))
                {
                    continue;
                }

                if (core.HasItem(def, stuff))
                {
                    Thing extracted = core.ExtractItem(def, count, stuff);
                    if (extracted != null)
                    {
                        return extracted;
                    }
                }

                // 标记已搜索的地图
                searchedMaps.Add(core.Map);
            }
            
            return null;
        }

        private List<Building_StorageCore> globalCores = new List<Building_StorageCore>();
        private Dictionary<Map, List<Building_StorageCore>> coresByMap = new Dictionary<Map, List<Building_StorageCore>>();
    }
}

