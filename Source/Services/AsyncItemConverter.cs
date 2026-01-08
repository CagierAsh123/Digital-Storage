using System;
using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Components;
using DigitalStorage.Data;
using DigitalStorage.Settings;
using RimWorld;
using Verse;

namespace DigitalStorage.Services
{
    /// <summary>
    /// 异步物品转换器：分帧处理物品转换，减少 GC 压力
    /// 支持预留物品系统：只转换超出预留数量的部分
    /// </summary>
    public static class AsyncItemConverter
    {
        private static Dictionary<Building_StorageCore, ConversionState> activeConversions = new Dictionary<Building_StorageCore, ConversionState>();

        // 对象池：只用于 ItemData（临时容器），不用于 StoredItemData（会被存储）
        private static Stack<ItemData> _itemDataPool = new Stack<ItemData>(64);
 
        private sealed class ConversionState
        {
            public List<Thing> toConvert = new List<Thing>();
            public List<ItemData> preparedData = new List<ItemData>();  // 主线程准备的数据
            public int dataPreparedIndex = 0;  // 已准备数据的索引
            public int despawnIndex = 0;  // 已销毁物品的索引
            public int itemsPerFrame = 15;  // 每帧处理 15 个物品（数据准备 + 销毁）
        }
 
        // 存储物品数据（在主线程提取，避免后台线程访问 Unity 对象）
        private sealed class ItemData
        {
            public Thing thing;  // 保留引用用于 DeSpawn
            public StoredItemData data;  // 准备好的数据
        }

        // 从对象池获取或创建 ItemData
        private static ItemData GetOrCreateItemData(Thing thing, StoredItemData data)
        {
            if (_itemDataPool.Count > 0)
            {
                ItemData itemData = _itemDataPool.Pop();
                itemData.thing = thing;
                itemData.data = data;
                return itemData;
            }
            return new ItemData { thing = thing, data = data };
        }

        // 归还 ItemData 到池（StoredItemData 不归还，因为会被存储）
        private static void ReturnToPool(List<ItemData> items)
        {
            foreach (ItemData item in items)
            {
                // 只归还 ItemData（清空引用防止内存泄漏）
                if (_itemDataPool.Count < 64)
                {
                    item.thing = null;
                    item.data = null;
                    _itemDataPool.Push(item);
                }
            }
        }

        /// <summary>
        /// 开始异步转换物品（支持预留物品系统）
        /// </summary>
        public static void StartAsyncConversion(Building_StorageCore core)
        {
            if (DigitalStorageSettings.enableConversionLog)
            {
                Log.Message($"[数字存储] AsyncItemConverter.StartAsyncConversion: 开始, core={(core != null ? "存在" : "null")}, Powered={(core != null ? core.Powered.ToString() : "N/A")}");
            }
            
            if (core == null || !core.Powered)
            {
                if (DigitalStorageSettings.enableConversionLog)
                {
                    Log.Message($"[数字存储] AsyncItemConverter.StartAsyncConversion: 提前返回, core为null或未供电");
                }
                return;
            }

            SlotGroup slotGroup = core.GetSlotGroup();
            if (slotGroup == null)
            {
                if (DigitalStorageSettings.enableConversionLog)
                {
                    Log.Message($"[数字存储] AsyncItemConverter.StartAsyncConversion: SlotGroup为null");
                }
                return;
            }

            int reservedCount = DigitalStorageSettings.reservedCountPerItem;
            if (DigitalStorageSettings.enableConversionLog)
            {
                Log.Message($"[数字存储] AsyncItemConverter.StartAsyncConversion: 预留数量={reservedCount}");
            }

            // 按类型分组统计
            Dictionary<string, List<Thing>> itemsByKey = new Dictionary<string, List<Thing>>();
            foreach (Thing thing in slotGroup.HeldThings)
            {
                if (thing != null && thing.Spawned)
                {
                    string key = GetItemKey(thing);
                    if (!itemsByKey.ContainsKey(key))
                    {
                        itemsByKey[key] = new List<Thing>();
                    }
                    itemsByKey[key].Add(thing);
                }
            }

            if (DigitalStorageSettings.enableConversionLog)
            {
                Log.Message($"[数字存储] AsyncItemConverter.StartAsyncConversion: 统计完成, 物品类型数={itemsByKey.Count}");
            }

            // 对每种类型，只转换超出预留数量的部分
            List<Thing> toConvert = new List<Thing>();
            foreach (var kvp in itemsByKey)
            {
                List<Thing> things = kvp.Value;

                // 计算总数量
                int totalCount = things.Sum(t => t.stackCount);
                
                if (DigitalStorageSettings.enableConversionLog)
                {
                    Log.Message($"[数字存储] AsyncItemConverter: Key={kvp.Key}, 总数量={totalCount}, 预留={reservedCount}");
                }

                // 如果总数超过预留数量，转换超出部分
                if (totalCount > reservedCount)
                {
                    int toConvertCount = totalCount - reservedCount;
                    int converted = 0;
                    
                    if (DigitalStorageSettings.enableConversionLog)
                    {
                        Log.Message($"[数字存储] AsyncItemConverter: 超出预留, 需转换={toConvertCount}");
                    }

                    foreach (Thing thing in things.OrderBy(t => t.stackCount))
                    {
                        if (converted >= toConvertCount)
                        {
                            break;
                        }

                        int remaining = toConvertCount - converted;
                        if (thing.stackCount <= remaining)
                        {
                            // 整个物品都转换
                            toConvert.Add(thing);
                            converted += thing.stackCount;
                        }
                        else
                        {
                            // 只转换部分
                            // 注意：SplitOff 返回的新 Thing 不会被自动 Spawn，需要手动 Spawn
                            Thing split = thing.SplitOff(remaining);
                            if (split != null && split.stackCount > 0)
                            {
                                // 关键：SplitOff 返回的物品需要 Spawn 才能被处理
                                if (!split.Spawned && thing.Map != null)
                                {
                                    GenSpawn.Spawn(split, thing.Position, thing.Map);
                                    if (DigitalStorageSettings.enableConversionLog)
                                    {
                                        Log.Message($"[数字存储] AsyncItemConverter: SplitOff后Spawn物品, {split.Label} x{split.stackCount}, 位置: {thing.Position}");
                                    }
                                }
                                toConvert.Add(split);
                                converted += split.stackCount;
                            }
                            else
                            {
                                if (DigitalStorageSettings.enableConversionLog)
                                {
                                    Log.Message($"[数字存储] AsyncItemConverter: SplitOff失败或无效, split={(split != null ? split.stackCount.ToString() : "null")}");
                                }
                            }
                        }
                    }
                    
                    if (DigitalStorageSettings.enableConversionLog)
                    {
                        Log.Message($"[数字存储] AsyncItemConverter: 已收集转换物品, 需转换={toConvertCount}, 已收集={converted}");
                    }
                }
            }

            if (toConvert.Count == 0)
            {
                if (DigitalStorageSettings.enableConversionLog)
                {
                    Log.Message($"[数字存储] AsyncItemConverter.StartAsyncConversion: 无需转换, toConvert.Count=0");
                }
                return;
            }
            
            if (DigitalStorageSettings.enableConversionLog)
            {
                Log.Message($"[数字存储] AsyncItemConverter.StartAsyncConversion: 准备转换 {toConvert.Count} 个堆叠");
            }

            // 如果已经有转换在进行，合并列表
            if (activeConversions.TryGetValue(core, out ConversionState existingState))
            {
                // 添加新物品到列表（去除重复）
                existingState.toConvert.AddRange(toConvert.Except(existingState.toConvert));
            }
            else
            {
                // 创建新的转换状态
                activeConversions[core] = new ConversionState
                {
                    toConvert = toConvert
                };
            }
        }

        /// <summary>
        /// 每帧更新：处理数据准备和物品销毁
        /// </summary>
        public static void Update()
        {
            if (activeConversions.Count == 0)
            {
                return;
            }

            List<Building_StorageCore> toRemove = new List<Building_StorageCore>();

            foreach (var kvp in activeConversions)
            {
                Building_StorageCore core = kvp.Key;
                ConversionState state = kvp.Value;

                if (core == null || !core.Spawned || !core.Powered)
                {
                    toRemove.Add(core);
                    continue;
                }

                // 阶段 1：在主线程准备数据（Thing 属性必须在主线程访问）
                // 分帧处理，避免单帧创建太多对象
                int prepared = 0;
                while (state.dataPreparedIndex < state.toConvert.Count && prepared < state.itemsPerFrame)
                {
                    Thing thing = state.toConvert[state.dataPreparedIndex];
                    
                    if (thing != null && thing.Spawned && !thing.Destroyed)
                    {
                        // 每次都创建新的 StoredItemData（会被存储到虚拟存储，不能复用）
                        StoredItemData data = new StoredItemData(thing);
                        ItemData itemData = GetOrCreateItemData(thing, data);
                        state.preparedData.Add(itemData);
                    }
                    
                    state.dataPreparedIndex++;
                    prepared++;
                }

                // 阶段 2：在主线程执行存储和销毁（Unity API 必须在主线程）
                // 使用准备好的数据，减少重复访问 Thing 属性
                int processed = 0;
                while (state.despawnIndex < state.preparedData.Count && processed < state.itemsPerFrame)
                {
                    ItemData itemData = state.preparedData[state.despawnIndex];
                    Thing thing = itemData.thing;
                    StoredItemData data = itemData.data;

                    if (thing != null && thing.Spawned && data != null)
                    {
                        // 存储到虚拟存储（使用准备好的数据，避免重复创建）
                        core.StoreItemData(data);
                        
                        // 销毁物品（Unity API，必须在主线程）
                        thing.DeSpawn(DestroyMode.Vanish);
                    }

                    state.despawnIndex++;
                    processed++;
                }

                // 如果处理完成，移除状态
                // 注意：需要同时检查数据准备完成和销毁完成
                if (state.dataPreparedIndex >= state.toConvert.Count &&
                    state.despawnIndex >= state.preparedData.Count)
                {
                    state.toConvert.Clear();
                    // 归还对象到池，减少 GC 压力
                    ReturnToPool(state.preparedData);
                    state.preparedData.Clear();
                    toRemove.Add(core);
                }
            }

            // 清理完成的状态
            foreach (Building_StorageCore core in toRemove)
            {
                activeConversions.Remove(core);
            }
        }

        /// <summary>
        /// 获取物品的唯一键（用于分组）
        /// </summary>
        private static string GetItemKey(Thing thing)
        {
            QualityCategory quality = thing.TryGetComp<CompQuality>()?.Quality ?? QualityCategory.Normal;
            string stuffName = thing.Stuff?.defName ?? "null";
            return $"{thing.def.defName}_{stuffName}_{quality}";
        }

        /// <summary>
        /// 检查是否有正在进行的转换
        /// </summary>
        public static bool IsConverting(Building_StorageCore core)
        {
            return core != null && activeConversions.ContainsKey(core);
        }

        /// <summary>
        /// 清理所有转换状态（用于游戏退出等场景）
        /// </summary>
        public static void ClearAll()
        {
            activeConversions.Clear();
            // 清理对象池，释放内存
            _itemDataPool.Clear();
        }
    }
}

