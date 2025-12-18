
// TODO: 待部分修改 - 新架构
// 需要优化的部分：
// 1. 保留预留物品系统（作为后备）
// 2. 优化虚拟存储逻辑
// 3. 确保预留物品不参与虚拟存储查找（仅作为后备）
// 4. 优化自动补货机制

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DigitalStorage.Data;
using DigitalStorage.Services;
using DigitalStorage.Settings;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    [StaticConstructorOnStartup]
    public class Building_StorageCore : Building_Storage, IRenameable
    {
        private static readonly Texture2D RenameTex = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", true);
        private const int BaseCapacity = 100;  // 基础容量：100 组物品
        private string networkName;
        private List<Building_Interface> interfaces = new List<Building_Interface>();
        private List<Building_OutputInterface> outputInterfaces = new List<Building_OutputInterface>();
        private List<Building_DiskCabinet> diskCabinets = new List<Building_DiskCabinet>();
        private CompPowerTrader powerComp;

        // 虚拟存储：不保存真实 Thing，只保存数据
        private List<StoredItemData> virtualStorage = new List<StoredItemData>();
        private Dictionary<string, StoredItemData> itemLookup = new Dictionary<string, StoredItemData>();

        // 预留物品系统：记录每种物品的预留数量（真实物品）
        // Key: "defName_stuffDefName_quality" 格式的字符串
        private Dictionary<string, int> reservedItemCounts = new Dictionary<string, int>();

        public string RenamableLabel
        {
            get { return this.networkName ?? this.LabelCapNoCount; }
            set { this.networkName = value; }
        }

        public string BaseLabel
        {
            get { return this.LabelCapNoCount; }
        }

        public string InspectLabel
        {
            get { return this.LabelCap; }
        }

        public string NetworkName
        {
            get { return this.networkName ?? "未命名网络"; }
            set { this.networkName = value; }
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
            DigitalStorageMapComponent component = map.GetComponent<DigitalStorageMapComponent>();
            if (component != null)
            {
                component.RegisterCore(this);
            }
            Game game = Current.Game;
            if (game != null)
            {
                DigitalStorageGameComponent component2 = game.GetComponent<DigitalStorageGameComponent>();
                if (component2 != null)
                {
                    component2.RegisterCore(this);
                }
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
                    component.DeregisterCore(this);
                }
            }
            Game game = Current.Game;
            if (game != null)
            {
                DigitalStorageGameComponent component2 = game.GetComponent<DigitalStorageGameComponent>();
                if (component2 != null)
                {
                    component2.DeregisterCore(this);
                }
            }
            foreach (Building_Interface port in this.interfaces.ToList())
            {
                if (port != null)
                {
                    port.SetBoundCore(null);
                }
            }
            this.interfaces.Clear();
            
            foreach (Building_OutputInterface outPort in this.outputInterfaces.ToList())
            {
                if (outPort != null)
                {
                    outPort.SetBoundCore(null);
                }
            }
            this.outputInterfaces.Clear();
            
            foreach (Building_DiskCabinet cabinet in this.diskCabinets.ToList())
            {
                if (cabinet != null)
                {
                    cabinet.SetBoundCore(null);
                }
            }
            this.diskCabinets.Clear();

            base.DeSpawn(mode);
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo gizmo in base.GetGizmos())
            {
                yield return gizmo;
            }
            
            yield return new Command_Action
            {
                defaultLabel = "查看存储",
                defaultDesc = "查看虚拟存储中的所有物品",
                icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", true),
                action = delegate()
                {
                    Find.WindowStack.Add(new DigitalStorage.UI.Dialog_VirtualStorage(this));
                }
            };
            
            yield return new Command_Action
            {
                defaultLabel = "重命名网络",
                defaultDesc = "为此存储网络设置一个名称",
                icon = RenameTex,
                action = delegate()
                {
                    Find.WindowStack.Add(new Dialog_RenameNetwork(this));
                }
            };
            yield break;
        }

        public override void PostMake()
        {
            base.PostMake();
            this.interfaces = new List<Building_Interface>();
            this.outputInterfaces = new List<Building_OutputInterface>();
            this.diskCabinets = new List<Building_DiskCabinet>();
            this.virtualStorage = new List<StoredItemData>();
            this.itemLookup = new Dictionary<string, StoredItemData>();
            this.reservedItemCounts = new Dictionary<string, int>();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look<string>(ref this.networkName, "networkName", null, false);
            Scribe_Collections.Look<Building_Interface>(ref this.interfaces, "interfaces", LookMode.Reference, Array.Empty<object>());
            Scribe_Collections.Look<Building_OutputInterface>(ref this.outputInterfaces, "outputInterfaces", LookMode.Reference, Array.Empty<object>());
            Scribe_Collections.Look<Building_DiskCabinet>(ref this.diskCabinets, "diskCabinets", LookMode.Reference, Array.Empty<object>());
            Scribe_Collections.Look<StoredItemData>(ref this.virtualStorage, "virtualStorage", LookMode.Deep);
            Scribe_Collections.Look<string, int>(ref this.reservedItemCounts, "reservedItemCounts", LookMode.Value, LookMode.Value);
            
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (this.interfaces == null)
                {
                    this.interfaces = new List<Building_Interface>();
                }
                if (this.outputInterfaces == null)
                {
                    this.outputInterfaces = new List<Building_OutputInterface>();
                }
                if (this.diskCabinets == null)
                {
                    this.diskCabinets = new List<Building_DiskCabinet>();
                }
                if (this.virtualStorage == null)
                {
                    this.virtualStorage = new List<StoredItemData>();
                }
                if (this.reservedItemCounts == null)
                {
                    this.reservedItemCounts = new Dictionary<string, int>();
                }
                
                // 重建查找表
                RebuildLookup();
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
            sb.AppendLine("网络: " + this.NetworkName);
            sb.AppendLine(string.Format("容量: {0}/{1} 组", this.GetUsedCapacity(), this.GetCapacity()));
            sb.AppendLine(string.Format("磁盘柜: {0}", this.diskCabinets.Count));
            sb.AppendLine(string.Format("输入接口: {0}", this.interfaces.Count));
            sb.AppendLine(string.Format("输出接口: {0}", this.outputInterfaces.Count));
            if (!this.Powered)
            {
                sb.AppendLine("无电力");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 获取容量（单位：组）
        /// </summary>
        public int GetCapacity()
        {
            return BaseCapacity + this.GetDiskCapacity();
        }

        /// <summary>
        /// 获取已使用容量（单位：组）
        /// </summary>
        public int GetUsedCapacity()
        {
            // 返回虚拟存储中的组数，而不是物品数量
            return this.virtualStorage.Count;
        }

        public int GetDiskCapacity()
        {
            int total = 0;
            foreach (Building_DiskCabinet cabinet in this.diskCabinets)
            {
                if (cabinet != null && cabinet.Spawned)
                {
                    total += cabinet.GetProvidedCapacity();
                }
            }
            return total;
        }

        /// <summary>
        /// 重写 Accepts 方法，这是原版系统检查存储是否接受物品的标准方法
        /// 注意：原版方法不是 virtual，所以使用 new 隐藏基类方法
        /// </summary>
        public new bool Accepts(Thing t)
        {
            // 首先检查存储设置（原版逻辑）
            if (!base.Accepts(t))
            {
                return false;
            }

            // 检查电力
            if (!this.Powered)
            {
                return false;
            }

            // 检查是否能合并到已有组
            bool canMerge = false;
            foreach (StoredItemData existing in this.virtualStorage)
            {
                if (existing.Matches(t.def, t.Stuff) && 
                    existing.quality == (t.TryGetComp<CompQuality>()?.Quality ?? QualityCategory.Normal))
                {
                    // 可以合并到已有组，不占用新组
                    canMerge = true;
                    break;
                }
            }

            // 如果能合并，不占用新组；如果不能合并，需要检查是否有空组
            if (canMerge)
            {
                return true;  // 可以合并，不占用新组
            }
            else
            {
                // 不能合并，需要新组，检查容量
                return this.GetUsedCapacity() < this.GetCapacity();
            }
        }

        public bool CanReceiveThing(Thing item)
        {
            return this.Accepts(item);
        }

        public void RegisterInterface(Building_Interface port)
        {
            if (port != null && !this.interfaces.Contains(port))
            {
                this.interfaces.Add(port);
            }
        }

        public void DeregisterInterface(Building_Interface port)
        {
            this.interfaces.Remove(port);
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
            this.outputInterfaces.Remove(port);
        }

        public IEnumerable<Building_OutputInterface> GetAllOutputInterfaces()
        {
            return this.outputInterfaces;
        }

        public void RegisterDiskCabinet(Building_DiskCabinet cabinet)
        {
            if (cabinet != null && !this.diskCabinets.Contains(cabinet))
            {
                this.diskCabinets.Add(cabinet);
            }
        }

        public void DeregisterDiskCabinet(Building_DiskCabinet cabinet)
        {
            this.diskCabinets.Remove(cabinet);
        }

        // 记录上次物品数量，用于延迟转换
        private int lastPhysicalItemCount = 0;

        protected override void Tick()
        {
            base.Tick();
            
            if (!this.Powered)
            {
                return;
            }

            // 每 60 tick 检查一次（1秒），维护预留物品
            if (Find.TickManager.TicksGame % 60 == 0)
            {
                MaintainReservedItems();
            }
            
            // 每 300 tick 检查一次（5秒），减少 GC 压力
            // 检查是否有物品超过预留数量，如果有就转换
            if (Find.TickManager.TicksGame % 300 == 0)
            {
                SlotGroup slotGroup = base.GetSlotGroup();
                if (slotGroup != null)
                {
                    int reservedCount = DigitalStorageSettings.reservedCountPerItem;
                    
                    // 检查是否有任何物品超过预留数量
                    bool needsConversion = false;
                    Dictionary<string, int> itemCounts = new Dictionary<string, int>();
                    
                    foreach (Thing thing in slotGroup.HeldThings)
                    {
                        if (thing == null || !thing.Spawned)
                        {
                            continue;
                        }
                        
                        string key = GetItemKey(thing);
                        if (!itemCounts.ContainsKey(key))
                        {
                            itemCounts[key] = 0;
                        }
                        itemCounts[key] += thing.stackCount;
                        
                        // 如果任何物品超过预留数量，需要转换
                        if (itemCounts[key] > reservedCount)
                        {
                            needsConversion = true;
                            break;
                        }
                    }
                    
                    if (needsConversion)
                    {
                        // 使用异步转换，分帧处理，减少单帧 GC 压力
                        // 注意：转换时会保留预留数量
                        Services.AsyncItemConverter.StartAsyncConversion(this);
                    }
                    
                    // 更新物品数量记录（用于其他用途）
                    lastPhysicalItemCount = slotGroup.HeldThings.Count();
                }
            }
        }

        /// <summary>
        /// 维护预留物品：检查预留数量，低于阈值时从虚拟存储补充
        /// </summary>
        private void MaintainReservedItems()
        {
            int reservedCount = DigitalStorageSettings.reservedCountPerItem;
            SlotGroup slotGroup = base.GetSlotGroup();
            if (slotGroup == null)
            {
                return;
            }

            // 统计当前真实物品数量（按类型分组）
            Dictionary<string, int> currentPhysicalCounts = new Dictionary<string, int>();
            foreach (Thing thing in slotGroup.HeldThings)
            {
                if (thing == null || !thing.Spawned)
                {
                    continue;
                }

                string key = GetItemKey(thing);
                if (!currentPhysicalCounts.ContainsKey(key))
                {
                    currentPhysicalCounts[key] = 0;
                }
                currentPhysicalCounts[key] += thing.stackCount;
            }

            // 检查每种物品的预留数量
            foreach (var kvp in currentPhysicalCounts)
            {
                string key = kvp.Key;
                int currentCount = kvp.Value;

                // 如果当前数量低于预留阈值，从虚拟存储补充
                if (currentCount < reservedCount)
                {
                    int needed = reservedCount - currentCount;
                    ThingDef def = GetDefFromKey(key);
                    ThingDef stuff = GetStuffFromKey(key);
                    QualityCategory quality = GetQualityFromKey(key);

                    // 从虚拟存储提取
                    Thing extracted = ExtractItemForReserved(def, needed, stuff, quality);
                    if (extracted != null)
                    {
                        // 生成到核心位置
                        GenSpawn.Spawn(extracted, this.Position, this.Map);
                        if (DigitalStorageSettings.enableDebugLog)
                        {
                            Log.Message($"[数字存储] 补充预留物品: {extracted.Label} x{extracted.stackCount}");
                        }
                    }
                }
            }

            // 更新预留物品计数
            this.reservedItemCounts = currentPhysicalCounts;
        }

        /// <summary>
        /// 获取物品的唯一键（用于分组）
        /// </summary>
        private string GetItemKey(Thing thing)
        {
            QualityCategory quality = thing.TryGetComp<CompQuality>()?.Quality ?? QualityCategory.Normal;
            string stuffName = thing.Stuff?.defName ?? "null";
            return $"{thing.def.defName}_{stuffName}_{quality}";
        }

        private string GetItemKey(ThingDef def, ThingDef stuff, QualityCategory quality)
        {
            string stuffName = stuff?.defName ?? "null";
            return $"{def.defName}_{stuffName}_{quality}";
        }

        private ThingDef GetDefFromKey(string key)
        {
            string[] parts = key.Split('_');
            if (parts.Length >= 1)
            {
                return DefDatabase<ThingDef>.GetNamedSilentFail(parts[0]);
            }
            return null;
        }

        private ThingDef GetStuffFromKey(string key)
        {
            string[] parts = key.Split('_');
            if (parts.Length >= 2 && parts[1] != "null")
            {
                return DefDatabase<ThingDef>.GetNamedSilentFail(parts[1]);
            }
            return null;
        }

        private QualityCategory GetQualityFromKey(string key)
        {
            string[] parts = key.Split('_');
            if (parts.Length >= 3 && Enum.TryParse<QualityCategory>(parts[2], out QualityCategory quality))
            {
                return quality;
            }
            return QualityCategory.Normal;
        }

        /// <summary>
        /// 将核心中的真实物品转换为虚拟数据（只转换超出预留数量的部分）
        /// </summary>
        public void ConvertPhysicalItemsToVirtualWithReserved()
        {
            SlotGroup slotGroup = base.GetSlotGroup();
            if (slotGroup == null) return;

            int reservedCount = DigitalStorageSettings.reservedCountPerItem;

            // 第一步：完成所有统计（先统计完，再计算）
            Dictionary<string, int> totalCountsByKey = new Dictionary<string, int>();
            Dictionary<string, List<Thing>> thingsByKey = new Dictionary<string, List<Thing>>();
            
            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] ========== 开始统计阶段 ==========");
            }
            
            foreach (Thing thing in slotGroup.HeldThings)
            {
                if (thing == null || !thing.Spawned)
                {
                    continue;
                }

                string key = GetItemKey(thing);
                if (!totalCountsByKey.ContainsKey(key))
                {
                    totalCountsByKey[key] = 0;
                    thingsByKey[key] = new List<Thing>();
                }
                
                int stackCount = thing.stackCount;
                totalCountsByKey[key] += stackCount;
                thingsByKey[key].Add(thing);
                
                if (DigitalStorageSettings.enableDebugLog)
                {
                    ThingDef def = GetDefFromKey(key);
                    Log.Message($"[数字存储] [统计阶段] 发现堆叠: {thing.Label} x{stackCount}, 位置: {thing.Position}, Key: {key}, 该Key累计总计: {totalCountsByKey[key]}, Thing对象ID: {thing.GetHashCode()}");
                }
            }
            
            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] ========== 统计阶段完成 ==========");
            }

            // 第二步：根据统计结果进行计算和转换
            // 简单逻辑：确认设定的预留量，比较实际量，如果实际>设定，将（实际-设定）的物品数字化
            List<Thing> toConvert = new List<Thing>();
            
            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] ========== 开始转换阶段 ==========");
            }
            
            foreach (var kvp in totalCountsByKey)
            {
                string key = kvp.Key;
                List<Thing> currentThings = thingsByKey[key];
                int statStageTotal = kvp.Value; // 统计阶段的总数
                
                // 重新统计实际数量（确保统计到预留所在堆叠的最新数量）
                int totalCount = 0;
                if (DigitalStorageSettings.enableDebugLog)
                {
                    Log.Message($"[数字存储] [转换阶段] 开始重新统计Key: {key}, 统计阶段总数: {statStageTotal}, 堆叠数: {currentThings.Count}");
                }
                
                foreach (Thing thing in currentThings)
                {
                    if (thing != null && thing.Spawned)
                    {
                        int currentStackCount = thing.stackCount;
                        totalCount += currentStackCount;
                        if (DigitalStorageSettings.enableDebugLog)
                        {
                            Log.Message($"[数字存储] [转换阶段] 重新统计堆叠: {thing.Label} x{currentStackCount}, 位置: {thing.Position}, Thing对象ID: {thing.GetHashCode()}, 累计总计: {totalCount}");
                        }
                    }
                    else
                    {
                        if (DigitalStorageSettings.enableDebugLog)
                        {
                            Log.Message($"[数字存储] [转换阶段] 跳过无效堆叠: Thing对象ID: {(thing != null ? thing.GetHashCode().ToString() : "null")}, Spawned: {(thing != null ? thing.Spawned.ToString() : "null")}");
                        }
                    }
                }
                
                if (DigitalStorageSettings.enableDebugLog)
                {
                    ThingDef def = GetDefFromKey(key);
                    ThingDef stuff = GetStuffFromKey(key);
                    Log.Message($"[数字存储] [转换阶段] 重新统计完成: {def?.label ?? key}, 统计阶段总数: {statStageTotal}, 转换阶段总数: {totalCount}, 预留: {reservedCount}, 堆叠数: {currentThings.Count}, 差异: {totalCount - statStageTotal}");
                }

                // 检测：确认设定的预留量，比较实际量，如果实际>设定，将（实际-设定）的物品数字化
                if (totalCount > reservedCount)
                {
                    int toConvertCount = totalCount - reservedCount;
                    int converted = 0;
                    
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        ThingDef def = GetDefFromKey(key);
                        ThingDef stuff = GetStuffFromKey(key);
                        Log.Message($"[数字存储] [转换阶段] 检测到超出预留: {def?.label ?? key}, 实际: {totalCount}, 预留: {reservedCount}, 需转换: {toConvertCount}");
                    }

                    foreach (Thing thing in currentThings.OrderBy(t => t.stackCount))
                    {
                        if (converted >= toConvertCount)
                        {
                            if (DigitalStorageSettings.enableDebugLog)
                            {
                                Log.Message($"[数字存储] [转换阶段] 已收集足够数量，停止转换，已收集: {converted}, 需转换: {toConvertCount}");
                            }
                            break;
                        }

                        // 重新检查thing是否仍然有效（可能在循环过程中被其他逻辑处理）
                        if (thing == null || !thing.Spawned)
                        {
                            if (DigitalStorageSettings.enableDebugLog)
                            {
                                Log.Message($"[数字存储] [转换阶段] 跳过无效堆叠: Thing对象ID: {(thing != null ? thing.GetHashCode().ToString() : "null")}");
                            }
                            continue;
                        }

                        // 重新获取stackCount，确保是最新值
                        int currentStackCount = thing.stackCount;
                        int remaining = toConvertCount - converted;
                        
                        if (DigitalStorageSettings.enableDebugLog)
                        {
                            Log.Message($"[数字存储] [转换阶段] 处理堆叠: {thing.Label} x{currentStackCount}, 位置: {thing.Position}, Thing对象ID: {thing.GetHashCode()}, 剩余需转换: {remaining}, 已收集: {converted}");
                        }
                        
                        if (currentStackCount <= remaining)
                        {
                            // 整个物品都转换
                            toConvert.Add(thing);
                            converted += currentStackCount;
                            if (DigitalStorageSettings.enableDebugLog)
                            {
                                Log.Message($"[数字存储] [转换阶段] 整个堆叠转换: {thing.Label} x{currentStackCount}, 已收集: {converted}");
                            }
                        }
                        else
                        {
                            // 只转换部分
                            // 注意：SplitOff 返回的新 Thing 不会被自动 Spawn，所以不检查 Spawned
                            Thing split = thing.SplitOff(remaining);
                            if (split != null && split.stackCount > 0)
                            {
                                toConvert.Add(split);
                                converted += split.stackCount;
                                if (DigitalStorageSettings.enableDebugLog)
                                {
                                    Log.Message($"[数字存储] [转换阶段] 部分堆叠转换: 从 {thing.Label} x{currentStackCount} SplitOff {split.stackCount} 个, 原堆叠剩余: {thing.stackCount}, 已收集: {converted}");
                                }
                            }
                            else
                            {
                                if (DigitalStorageSettings.enableDebugLog)
                                {
                                    Log.Message($"[数字存储] [转换阶段] SplitOff失败: {thing.Label} x{currentStackCount}, SplitOff返回: {(split != null ? split.stackCount.ToString() : "null")}");
                                }
                            }
                        }
                    }
                    
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        ThingDef def = GetDefFromKey(key);
                        ThingDef stuff = GetStuffFromKey(key);
                        Log.Message($"[数字存储] [转换阶段] 转换收集完成: {def?.label ?? key}, 实际: {totalCount}, 预留: {reservedCount}, 需转换: {toConvertCount}, 已收集: {converted}, 差异: {toConvertCount - converted}");
                    }
                }
                else
                {
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        ThingDef def = GetDefFromKey(key);
                        ThingDef stuff = GetStuffFromKey(key);
                        Log.Message($"[数字存储] [转换阶段] 未超出预留: {def?.label ?? key}, 实际: {totalCount}, 预留: {reservedCount}, 无需转换");
                    }
                }
            }
            
            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] ========== 转换阶段完成 ==========");
            }

            if (toConvert.Count == 0)
            {
                return;
            }

            // 先存储所有数据（合并到虚拟存储）
            int totalConvertedCount = 0;
            foreach (Thing thing in toConvert)
            {
                if (thing != null && thing.stackCount > 0)
                {
                    int beforeCount = GetItemCount(thing.def, thing.Stuff);
                    StoreItem(thing);
                    int afterCount = GetItemCount(thing.def, thing.Stuff);
                    totalConvertedCount += thing.stackCount;
                    
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Message($"[数字存储] 转换物品: {thing.Label} x{thing.stackCount}, 转换前虚拟存储: {beforeCount}, 转换后虚拟存储: {afterCount}");
                    }
                }
            }

            // 再批量销毁（减少中间对象的 GC 压力）
            foreach (Thing thing in toConvert)
            {
                if (thing != null && thing.Spawned)
                {
                    thing.DeSpawn(DestroyMode.Vanish);
                }
            }

            if (DigitalStorageSettings.enableDebugLog && totalConvertedCount > 0)
            {
                Log.Message($"[数字存储] 本次转换总计: {totalConvertedCount} 个物品");
            }

            // 更新物品数量记录
            lastPhysicalItemCount = slotGroup.HeldThings.Count();
        }

        /// <summary>
        /// 存储物品到虚拟存储
        /// </summary>
        public void StoreItem(Thing thing)
        {
            if (thing == null) return;

            // 创建数据对象
            StoredItemData data = new StoredItemData(thing);
            StoreItemData(data);
        }

        /// <summary>
        /// 直接存储 StoredItemData 到虚拟存储（用于多线程优化）
        /// </summary>
        public void StoreItemData(StoredItemData data)
        {
            if (data == null || data.def == null) return;

            // 检查物品是否有品质组件（如钢铁等原材料没有品质）
            bool hasQuality = data.def.HasComp<CompQuality>();

            // 尝试合并到已有堆叠
            StoredItemData match = null;
            
            foreach (StoredItemData existing in this.virtualStorage)
            {
                if (existing.Matches(data.def, data.stuffDef))
                {
                    // 如果物品没有品质组件，忽略品质比较，直接匹配
                    if (!hasQuality)
                    {
                        match = existing;
                        break;
                    }
                    // 如果物品有品质组件，需要品质也匹配
                    else if (existing.quality == data.quality)
                    {
                        match = existing;
                        break;
                    }
                }
            }

            // 如果找到匹配，合并
            if (match != null)
            {
                match.stackCount += data.stackCount;
                // 如果 hitPoints 不同，取加权平均值
                if (data.hitPoints > 0 && match.hitPoints > 0)
                {
                    int totalHitPoints = (match.hitPoints * match.stackCount) + (data.hitPoints * data.stackCount);
                    int totalCount = match.stackCount + data.stackCount;
                    match.hitPoints = totalHitPoints / totalCount;
                }
                // 如果物品有品质组件，且新物品品质更低，更新为更低的品质（表示混合品质）
                if (hasQuality && data.quality < match.quality)
                {
                    match.quality = data.quality;
                }
                return;
            }

            // 没有匹配，创建新条目
            this.virtualStorage.Add(data);
            this.itemLookup[data.uniqueId] = data;
        }

        /// <summary>
        /// 从虚拟存储提取物品（用于预留物品补充）
        /// </summary>
        private Thing ExtractItemForReserved(ThingDef def, int count, ThingDef stuff, QualityCategory quality)
        {
            foreach (StoredItemData item in this.virtualStorage)
            {
                if (item.Matches(def, stuff) && item.quality == quality && item.stackCount >= count)
                {
                    item.stackCount -= count;
                    
                    if (item.stackCount <= 0)
                    {
                        this.virtualStorage.Remove(item);
                        this.itemLookup.Remove(item.uniqueId);
                    }

                    StoredItemData extractData = new StoredItemData
                    {
                        def = item.def,
                        stuffDef = item.stuffDef,
                        quality = item.quality,
                        hitPoints = item.hitPoints,
                        stackCount = count
                    };

                    return extractData.CreateThing();
                }
            }

            return null;
        }

        /// <summary>
        /// 从虚拟存储提取物品（通用方法）
        /// </summary>
        public Thing ExtractItem(ThingDef def, int count, ThingDef stuff = null)
        {
            foreach (StoredItemData item in this.virtualStorage)
            {
                if (item.Matches(def, stuff) && item.stackCount >= count)
                {
                    item.stackCount -= count;
                    
                    if (item.stackCount <= 0)
                    {
                        this.virtualStorage.Remove(item);
                        this.itemLookup.Remove(item.uniqueId);
                    }

                    StoredItemData extractData = new StoredItemData
                    {
                        def = item.def,
                        stuffDef = item.stuffDef,
                        quality = item.quality,
                        hitPoints = item.hitPoints,
                        stackCount = count
                    };

                    return extractData.CreateThing();
                }
            }

            return null;
        }

        /// <summary>
        /// 检查是否有指定物品（包括预留物品和虚拟存储）
        /// </summary>
        public bool HasItem(ThingDef def, ThingDef stuff = null)
        {
            // 检查预留物品
            SlotGroup slotGroup = base.GetSlotGroup();
            if (slotGroup != null)
            {
                foreach (Thing thing in slotGroup.HeldThings)
                {
                    if (thing != null && thing.Spawned && thing.def == def && thing.Stuff == stuff)
                    {
                        return true;
                    }
                }
            }

            // 检查虚拟存储
            foreach (StoredItemData item in this.virtualStorage)
            {
                if (item.Matches(def, stuff) && item.stackCount > 0)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// 获取指定物品的总数量（包括预留物品和虚拟存储）
        /// </summary>
        public int GetItemCount(ThingDef def, ThingDef stuff = null)
        {
            int total = 0;

            // 计算预留物品数量
            SlotGroup slotGroup = base.GetSlotGroup();
            if (slotGroup != null)
            {
                foreach (Thing thing in slotGroup.HeldThings)
                {
                    if (thing != null && thing.Spawned && thing.def == def && thing.Stuff == stuff)
                    {
                        total += thing.stackCount;
                    }
                }
            }

            // 计算虚拟存储数量
            foreach (StoredItemData item in this.virtualStorage)
            {
                if (item.Matches(def, stuff))
                {
                    total += item.stackCount;
                }
            }

            return total;
        }

        /// <summary>
        /// 获取预留物品的数量（不包括虚拟存储）
        /// </summary>
        public int GetReservedItemCount(ThingDef def, ThingDef stuff = null)
        {
            int total = 0;
            SlotGroup slotGroup = base.GetSlotGroup();
            if (slotGroup != null)
            {
                foreach (Thing thing in slotGroup.HeldThings)
                {
                    if (thing != null && thing.Spawned && thing.def == def && thing.Stuff == stuff)
                    {
                        total += thing.stackCount;
                    }
                }
            }
            return total;
        }

        /// <summary>
        /// 查找预留物品（返回第一个匹配的物品）
        /// </summary>
        public Thing FindReservedItem(ThingDef def, ThingDef stuff = null)
        {
            SlotGroup slotGroup = base.GetSlotGroup();
            if (slotGroup != null)
            {
                foreach (Thing thing in slotGroup.HeldThings)
                {
                    if (thing != null && thing.Spawned && thing.def == def && thing.Stuff == stuff)
                    {
                        return thing;
                    }
                }
            }
            return null;
        }

        /// <summary>
        /// 获取所有虚拟存储的物品（只读）
        /// </summary>
        public IEnumerable<StoredItemData> GetAllStoredItems()
        {
            return this.virtualStorage;
        }

        private void RebuildLookup()
        {
            this.itemLookup.Clear();
            foreach (StoredItemData item in this.virtualStorage)
            {
                if (!string.IsNullOrEmpty(item.uniqueId))
                {
                    this.itemLookup[item.uniqueId] = item;
                }
            }
        }
    }
}

