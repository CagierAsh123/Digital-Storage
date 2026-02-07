
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
using Verse.AI;

namespace DigitalStorage.Components
{
    [StaticConstructorOnStartup]
    public class Building_StorageCore : Building_Storage, IRenameable
    {
        private static readonly Texture2D RenameTex = ContentFinder<Texture2D>.Get("UI/Buttons/Rename", true);
        
        // 三层贴图（基座由graphicData处理，这里只需要光束和球）
        private static readonly Material LightMat = MaterialPool.MatFrom("2.0/一束光", ShaderDatabase.MoteGlow);
        private static readonly Material OrbMat = MaterialPool.MatFrom("2.0/一个球", ShaderDatabase.Cutout);
        private const int BaseCapacity = 100;  // 基础容量：100 组物品
        private string networkName;
        private List<Building_DiskCabinet> diskCabinets = new List<Building_DiskCabinet>();
        private CompPowerTrader powerComp;

        // 虚拟存储：不保存真实 Thing，只保存数据
        private List<StoredItemData> virtualStorage = new List<StoredItemData>();
        private Dictionary<string, StoredItemData> itemLookup = new Dictionary<string, StoredItemData>();

        // 预留物品系统：记录每种物品的预留数量（真实物品）
        // Key: "defName_stuffDefName_quality" 格式的字符串
        // 可能是dead store，但是我不敢动 - 存档兼容字段
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
            get { return this.networkName ?? "DS_UnnamedNetwork".Translate(); }
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

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            // 基座由 graphicData 绘制到地图网格，这里只绘制光束和球
            base.DrawAt(drawLoc, flip);
            
            // 绘制光束（中间层）
            Vector3 lightPos = drawLoc;
            lightPos.y = AltitudeLayer.BuildingOnTop.AltitudeFor();
            Matrix4x4 lightMat = Matrix4x4.TRS(lightPos, Quaternion.identity, new Vector3(3f, 10f, 3f));
            Graphics.DrawMesh(MeshPool.plane10, lightMat, LightMat, 0);
            
            // 绘制浮动球（顶层，上下浮动）
            float floatOffset = Mathf.Sin(Time.realtimeSinceStartup * 2f) * 0.15f;
            Vector3 orbPos = drawLoc;
            orbPos.y = AltitudeLayer.BuildingOnTop.AltitudeFor() + 0.01f;
            orbPos.z += floatOffset;
            Matrix4x4 orbMat = Matrix4x4.TRS(orbPos, Quaternion.identity, new Vector3(3f, 10f, 3f));
            Graphics.DrawMesh(MeshPool.plane10, orbMat, OrbMat, 0);
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
                defaultLabel = "DS_ViewStorage".Translate(),
                defaultDesc = "DS_ViewStorageDesc".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", true),
                action = delegate()
                {
                    Find.WindowStack.Add(new DigitalStorage.UI.Dialog_VirtualStorage(this));
                }
            };
            
            yield return new Command_Action
            {
                defaultLabel = "DS_RenameNetwork".Translate(),
                defaultDesc = "DS_RenameNetworkDesc".Translate(),
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
            this.diskCabinets = new List<Building_DiskCabinet>();
            this.virtualStorage = new List<StoredItemData>();
            this.itemLookup = new Dictionary<string, StoredItemData>();
            this.reservedItemCounts = new Dictionary<string, int>();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look<string>(ref this.networkName, "networkName", null, false);
            Scribe_Collections.Look<Building_DiskCabinet>(ref this.diskCabinets, "diskCabinets", LookMode.Reference, Array.Empty<object>());
            Scribe_Collections.Look<StoredItemData>(ref this.virtualStorage, "virtualStorage", LookMode.Deep);
            Scribe_Collections.Look<string, int>(ref this.reservedItemCounts, "reservedItemCounts", LookMode.Value, LookMode.Value);
            
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
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
            sb.AppendLine("DS_InspectNetwork".Translate(this.NetworkName));
            sb.AppendLine("DS_InspectCapacity".Translate(this.GetUsedCapacity(), this.GetCapacity()));
            sb.AppendLine("DS_InspectDiskCabinets".Translate(this.diskCabinets.Count));
            if (!this.Powered)
            {
                sb.AppendLine("DS_NoPower".Translate());
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 获取容量（单位：组）
        /// </summary>
        public int GetCapacity()
        {
            CompStorageCoreUpgrade upgradeComp = this.GetComp<CompStorageCoreUpgrade>();
            int baseCapacity = upgradeComp != null ? upgradeComp.GetCapacity() : BaseCapacity;
            return baseCapacity + this.GetDiskCapacity();
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

        // 对象池优化：缓存 Dictionary，避免频繁分配（通过 Clear() 复用）
        private Dictionary<string, int> _cachedItemCounts = new Dictionary<string, int>();
        private Dictionary<string, int> _cachedPhysicalCounts = new Dictionary<string, int>();
 
        // 可能是dead store，但是我不敢动
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
                if (DigitalStorageSettings.enableConversionLog)
                {
                    Log.Message($"[数字存储] Tick检查: TicksGame={Find.TickManager.TicksGame}, Powered={this.Powered}");
                }
                
                SlotGroup slotGroup = base.GetSlotGroup();
                if (slotGroup != null)
                {
                    int reservedCount = DigitalStorageSettings.reservedCountPerItem;
                    int totalItems = slotGroup.HeldThings.Count();
                    
                    if (DigitalStorageSettings.enableConversionLog)
                    {
                        Log.Message($"[数字存储] Tick检查: SlotGroup存在, 预留数量={reservedCount}, 物品堆叠数={totalItems}");
                    }
                    
                    // 检查是否有任何物品超过预留数量
                    bool needsConversion = false;
                    _cachedItemCounts.Clear();  // 复用 Dictionary，避免分配
                    
                    // 优化：用 List 遍历避免枚举器分配
                    List<Thing> heldThings = slotGroup.HeldThings.ToList();
                    for (int i = 0; i < heldThings.Count; i++)
                    {
                        Thing thing = heldThings[i];
                        if (thing == null || !thing.Spawned)
                        {
                            continue;
                        }
                        
                        // ⚠️ Bug修复：跳过被有效Job预约的物品
                        // 防止补货材料在pawn到达前被转换销毁
                        if (this.Map != null)
                        {
                            if (this.Map.reservationManager.IsReservedByAnyoneOf(new LocalTargetInfo(thing), Faction.OfPlayer))
                            {
                                // 安全检查：验证预约的Job是否仍然有效
                                // 防止Job取消后预约未释放导致的堆积
                                if (IsValidReservation(thing, this.Map))
                                {
                                    if (DigitalStorageSettings.enableConversionLog)
                                    {
                                        Log.Message($"[数字存储] Tick检查: 跳过被有效预约的物品, {thing.Label}");
                                    }
                                    continue; // 被有效预约的物品不参与转换检查
                                }
                                // 如果预约无效，继续处理（允许转换，防止堆积）
                                if (DigitalStorageSettings.enableConversionLog)
                                {
                                    Log.Message($"[数字存储] Tick检查: 预约无效，允许转换, {thing.Label}");
                                }
                            }
                        }
                        
                        string key = GetItemKey(thing);
                        if (!_cachedItemCounts.ContainsKey(key))
                        {
                            _cachedItemCounts[key] = 0;
                        }
                        _cachedItemCounts[key] += thing.stackCount;
                        
                        // 如果任何物品超过预留数量，需要转换
                        if (_cachedItemCounts[key] > reservedCount)
                        {
                            needsConversion = true;
                            if (DigitalStorageSettings.enableConversionLog)
                            {
                                Log.Message($"[数字存储] Tick检查: 发现超出预留的物品, Key={key}, 数量={_cachedItemCounts[key]}, 预留={reservedCount}");
                            }
                            break;
                        }
                    }
                    
                    if (needsConversion)
                    {
                        if (DigitalStorageSettings.enableConversionLog)
                        {
                            Log.Message($"[数字存储] Tick检查: 调用AsyncItemConverter.StartAsyncConversion");
                        }
                        // 使用异步转换，分帧处理，减少单帧 GC 压力
                        // 注意：转换时会保留预留数量
                        Services.AsyncItemConverter.StartAsyncConversion(this);
                    }
                    else
                    {
                        if (DigitalStorageSettings.enableConversionLog)
                        {
                            Log.Message($"[数字存储] Tick检查: 无需转换");
                        }
                    }
                    
                    // 更新物品数量记录（用于其他用途）
                    lastPhysicalItemCount = heldThings.Count;
                }
                else
                {
                    if (DigitalStorageSettings.enableConversionLog)
                    {
                        Log.Message($"[数字存储] Tick检查: SlotGroup为null");
                    }
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
            _cachedPhysicalCounts.Clear();  // 复用 Dictionary，避免分配
            
            // 优化：用 List 遍历避免枚举器分配
            List<Thing> heldThings = slotGroup.HeldThings.ToList();
            for (int i = 0; i < heldThings.Count; i++)
            {
                Thing thing = heldThings[i];
                if (thing == null || !thing.Spawned)
                {
                    continue;
                }

                string key = GetItemKey(thing);
                if (!_cachedPhysicalCounts.ContainsKey(key))
                {
                    _cachedPhysicalCounts[key] = 0;
                }
                _cachedPhysicalCounts[key] += thing.stackCount;
            }

            // ⚠️ 修复：遍历虚拟存储中存在的所有物品类型
            // 只有虚拟存储中存在的物品才会触发补货检查
            // 使用 ToList() 避免在遍历时修改集合导致异常
            foreach (StoredItemData itemData in this.virtualStorage.ToList())
            {
                if (itemData == null || itemData.def == null || itemData.stackCount <= 0)
                {
                    continue;
                }

                string key = GetItemKey(itemData.def, itemData.stuffDef, itemData.quality);
                
                // 获取当前实际预留数量（如果不存在就是0）
                int currentCount = _cachedPhysicalCounts.ContainsKey(key) ? _cachedPhysicalCounts[key] : 0;

                // ⚠️ 修复：如果当前数量低于预留阈值，从虚拟存储补充
                // 即使当前数量为0（预留物品被全部拿走），也应该补货
                if (currentCount < reservedCount)
                {
                    int needed = reservedCount - currentCount;
                    ThingDef def = itemData.def;
                    ThingDef stuff = itemData.stuffDef;
                    QualityCategory quality = itemData.quality;

                    // 考虑物品的堆叠限制，避免生成超出stackLimit的物品
                    int stackLimit = def.stackLimit;
                    int actualNeeded = Math.Min(needed, stackLimit);

                    // 从虚拟存储提取
                    Thing extracted = ExtractItemForReserved(def, actualNeeded, stuff, quality);
                    if (extracted != null)
                    {
                        // 生成到核心位置
                        GenSpawn.Spawn(extracted, this.Position, this.Map);
                        if (DigitalStorageSettings.enableDebugLog)
                        {
                            Log.Message($"[数字存储] 补充预留物品: {extracted.Label} x{extracted.stackCount}, 当前={currentCount}, 需要={actualNeeded}/{needed}");
                        }
                    }
                }
            }

            // 更新预留物品计数（只更新当前实际存在的物品类型）
            foreach (var kvp in _cachedPhysicalCounts)
            {
                this.reservedItemCounts[kvp.Key] = kvp.Value;
            }
        }

        /// <summary>
        /// 立即补货指定物品：从虚拟存储提取并生成到核心位置
        /// 用于需要立即补货的情况（如建造时没有预留物品）
        /// </summary>
        /// <param name="def">物品定义</param>
        /// <param name="stuff">材质定义</param>
        /// <param name="needed">需要的数量</param>
        /// <param name="quality">质量等级</param>
        /// <returns>补货后的预留物品（如果补货成功），否则 null</returns>
        public Thing TryReplenishItem(ThingDef def, ThingDef stuff, int needed, QualityCategory quality)
        {
            if (def == null || needed <= 0 || !this.Powered)
            {
                return null;
            }

            // 考虑物品的堆叠限制，避免生成超出stackLimit的物品
            int stackLimit = def.stackLimit;
            int actualNeeded = Math.Min(needed, stackLimit);

            // 检查虚拟存储中是否有足够的物品
            Thing extracted = ExtractItemForReserved(def, actualNeeded, stuff, quality);
            if (extracted != null)
            {
                // 生成到核心位置
                GenSpawn.Spawn(extracted, this.Position, this.Map);
                if (DigitalStorageSettings.enableDebugLog)
                {
                    Log.Message($"[数字存储] TryReplenishItem: 立即补货成功, {extracted.Label} x{extracted.stackCount}");
                }

                // 返回补货后的物品作为预留物品
                return extracted;
            }
            else
            {
                if (DigitalStorageSettings.enableDebugLog)
                {
                    Log.Message($"[数字存储] TryReplenishItem: 虚拟存储不足，无法补货, 需要 {def.label} x{actualNeeded}");
                }
                return null;
            }
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
        /// 获取物品的唯一键（用于分组）
        /// </summary>
        private static string GetItemKey(Thing thing)
        {
            QualityCategory quality = thing.TryGetComp<CompQuality>()?.Quality ?? QualityCategory.Normal;
            string stuffName = thing.Stuff?.defName ?? "null";
            return $"{thing.def.defName}_{stuffName}_{quality}";
        }

        private static string GetItemKey(ThingDef def, ThingDef stuff, QualityCategory quality)
        {
            string stuffName = stuff?.defName ?? "null";
            return $"{def.defName}_{stuffName}_{quality}";
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
            StoredItemData match = this.virtualStorage.FirstOrDefault(existing =>
                existing.Matches(data.def, data.stuffDef) &&
                (!hasQuality || existing.quality == data.quality));

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
            int beforeCount = GetItemCount(def, stuff);
            
            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] ExtractItem: 开始提取, {def?.label ?? "null"} x{count}, stuff={stuff?.label ?? "null"}, 提取前虚拟存储数量={beforeCount}");
            }
            
            foreach (StoredItemData item in this.virtualStorage)
            {
                if (item.Matches(def, stuff) && item.stackCount >= count)
                {
                    item.stackCount -= count;
                    
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Message($"[数字存储] ExtractItem: 找到匹配物品, 提取前={item.stackCount + count}, 提取后={item.stackCount}, 提取数量={count}");
                    }
                    
                    if (item.stackCount <= 0)
                    {
                        this.virtualStorage.Remove(item);
                        this.itemLookup.Remove(item.uniqueId);
                        
                        if (DigitalStorageSettings.enableDebugLog)
                        {
                            Log.Message($"[数字存储] ExtractItem: 物品数量为0，已从虚拟存储移除");
                        }
                    }

                    StoredItemData extractData = new StoredItemData
                    {
                        def = item.def,
                        stuffDef = item.stuffDef,
                        quality = item.quality,
                        hitPoints = item.hitPoints,
                        stackCount = count
                    };

                    int afterCount = GetItemCount(def, stuff);
                    
                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Message($"[数字存储] ExtractItem: 提取完成, 提取前虚拟存储={beforeCount}, 提取后虚拟存储={afterCount}, 差异={beforeCount - afterCount}");
                    }

                    return extractData.CreateThing();
                }
            }

            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[数字存储] ExtractItem: 未找到匹配物品或数量不足, {def?.label ?? "null"} x{count}");
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
        /// 检查预约是否有效（防止Job取消后预约未释放导致的堆积）
        /// </summary>
        private bool IsValidReservation(Thing thing, Map map)
        {
            if (thing == null || map == null || map.reservationManager == null)
            {
                return false;
            }

            // 获取该物品的所有预约
            foreach (var reservation in map.reservationManager.ReservationsReadOnly)
            {
                if (reservation.Target.Thing == thing && reservation.Claimant != null)
                {
                    Pawn claimant = reservation.Claimant;
                    Job reservationJob = reservation.Job;
                    
                    if (claimant == null || reservationJob == null)
                    {
                        continue;
                    }
                    
                    // 检查预约者的当前Job是否还是这个预约的Job
                    // 如果Job被取消，CurJob应该不是这个Job了
                    if (claimant.CurJob == reservationJob)
                    {
                        return true; // 当前Job匹配，预约有效
                    }
                    
                    // 检查Job队列中是否还有这个Job
                    if (claimant.jobs != null && claimant.jobs.jobQueue != null)
                    {
                        foreach (var queuedJob in claimant.jobs.jobQueue)
                        {
                            if (queuedJob.job == reservationJob)
                            {
                                return true; // Job在队列中，预约有效
                            }
                        }
                    }
                }
            }
            return false; // 没有有效预约（Job已取消或不存在）
        }

        /// <summary>
        /// 获取所有虚拟存储的物品（只读）
        /// </summary>
        public IEnumerable<StoredItemData> GetAllStoredItems()
        {
            return this.virtualStorage;
        }

        /// <summary>
        /// 获取虚拟存储中指定物品的数量（不含预留物品）
        /// </summary>
        public int GetVirtualItemCount(ThingDef def)
        {
            int total = 0;
            foreach (StoredItemData item in this.virtualStorage)
            {
                if (item.def == def && item.stackCount > 0)
                {
                    total += item.stackCount;
                }
            }
            return total;
        }

        /// <summary>
        /// 从虚拟存储中按 ThingDef 扣除指定数量（忽略 stuff，用于轨道交易）
        /// 返回实际扣除的数量
        /// </summary>
        public int DeductVirtualItems(ThingDef def, int count)
        {
            if (def == null || count <= 0) return 0;

            int remaining = count;

            // 使用 ToList 避免遍历时修改集合
            foreach (StoredItemData item in this.virtualStorage.ToList())
            {
                if (item.def != def || item.stackCount <= 0)
                {
                    continue;
                }

                if (remaining <= 0)
                {
                    break;
                }

                int toDeduct = System.Math.Min(item.stackCount, remaining);
                item.stackCount -= toDeduct;
                remaining -= toDeduct;

                if (item.stackCount <= 0)
                {
                    this.virtualStorage.Remove(item);
                    this.itemLookup.Remove(item.uniqueId);
                }
            }

            return count - remaining;
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

