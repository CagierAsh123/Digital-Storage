# Digital Storage Mod 架构解析文档

## 一、总体设计

### 1.1 核心概念
这是一个受 Minecraft 应用能源2（AE2）启发的 RimWorld 数字存储系统 mod。核心思想是将物品"数字化"存储，避免物理渲染和 Tick 开销，同时提供远程访问能力。

### 1.2 设计理念
- **虚拟存储**：物品转换为数据结构（StoredItemData），不保存真实 Thing 对象
- **预留物品系统**：保留少量物理物品用于兼容原版搬运系统
- **跨地图访问**：通过终端芯片植入体实现远程访问
- **分帧处理**：使用异步转换器减少 GC 压力和卡顿
- **电力驱动**：所有功能需要电力支持

### 1.3 技术架构
```
游戏层 (Game Component)
    ├── DigitalStorageGameComponent (全局核心管理)
    └── DigitalStorageMapComponent (地图级核心管理)

建筑层 (Buildings)
    ├── Building_StorageCore (存储核心)
    ├── Building_InputInterface (输入接口)
    └── Building_DiskCabinet (磁盘柜，未在代码中看到)

数据层 (Data)
    └── StoredItemData (虚拟物品数据)

服务层 (Services)
    ├── AsyncItemConverter (异步物品转换)
    ├── PawnStorageAccess (Pawn 存储访问)
    ├── CaravanStorageAccess (远行队存储访问)
    └── ObjectPool (对象池优化)

补丁层 (Harmony Patches)
    ├── 工作台制作补丁
    ├── 建造材料补丁
    ├── 交易系统补丁
    ├── 资源统计补丁
    └── Mod 兼容性补丁
```

---

## 二、核心组件详解

### 2.1 Building_StorageCore (存储核心)
**文件位置**: `Source/Components/Building_StorageCore.cs`

**核心功能**:
1. **虚拟存储管理**
   - 使用 `List<StoredItemData>` 存储虚拟物品
   - 使用 `Dictionary<string, StoredItemData>` 快速查找
   - 支持物品合并、提取、查询

2. **预留物品系统**
   - 每种物品保留可配置数量的物理物品（默认100个）
   - 自动补货机制：每60 tick检查一次，低于阈值时从虚拟存储补充
   - 防止Job取消后预约未释放导致的堆积

3. **容量管理**
   - 基础容量：100组物品
   - 通过 CompStorageCoreUpgrade 支持4级升级（100→250→500→1000）
   - 容量单位是"组"，不是"个"

4. **自动转换**
   - 每300 tick检查一次物理物品数量
   - 超过预留数量的部分自动转换为虚拟存储
   - 使用 AsyncItemConverter 分帧处理，避免卡顿

5. **三层渲染**
   - 基座：由 graphicData 处理
   - 光束：中间层，使用 MoteGlow shader
   - 浮动球：顶层，带上下浮动动画

**关键方法**:
- `StoreItem(Thing)`: 存储物品到虚拟存储
- `ExtractItem(ThingDef, int, ThingDef)`: 从虚拟存储提取物品
- `GetItemCount(ThingDef, ThingDef)`: 获取物品总数（预留+虚拟）
- `TryReplenishItem()`: 立即补货（用于建造等场景）
- `MaintainReservedItems()`: 维护预留物品数量

---

### 2.2 Building_InputInterface (输入接口)
**文件位置**: `Source/Components/Building_InputInterface.cs`

**核心功能**:
1. **自动连接**
   - 放置时自动连接相邻的存储核心
   - 支持手动选择连接目标

2. **物品传送**
   - 方案A（默认）：即时数字化，物品放上去立即转换
   - 方案B：先传送到核心附近，再由核心处理

3. **存储设置同步**
   - 自动同步绑定核心的存储设置
   - 支持原版存储优先级系统

**关键方法**:
- `Notify_ReceivedThing(Thing)`: 物品放上接口时触发
- `SetBoundCore(Building_StorageCore)`: 绑定核心
- `TryAutoConnect()`: 自动连接相邻核心

---

### 2.3 StoredItemData (虚拟物品数据)
**文件位置**: `Source/Data/StoredItemData.cs`

**数据结构**:
```csharp
public class StoredItemData {
    ThingDef def;           // 物品定义
    ThingDef stuffDef;      // 材质定义
    QualityCategory quality; // 品质
    int hitPoints;          // 耐久度
    int stackCount;         // 数量
    string uniqueId;        // 唯一标识
}
```

**核心功能**:
- 轻量级数据存储，不保存完整 Thing 对象
- 支持序列化（IExposable）
- 可根据数据重建真实 Thing 对象

---

### 2.4 AsyncItemConverter (异步物品转换器)
**文件位置**: `Source/Services/AsyncItemConverter.cs`

**核心功能**:
1. **分帧处理**
   - 每帧处理15个物品，避免单帧卡顿
   - 分两阶段：数据准备 → 物品销毁

2. **预留物品保护**
   - 只转换超出预留数量的部分
   - 按物品类型分组统计

3. **对象池优化**
   - 复用 ItemData 对象，减少 GC 压力
   - 复用 Dictionary，避免频繁分配

**工作流程**:
```
1. StartAsyncConversion() - 收集需要转换的物品
2. Update() - 每帧处理
   ├── 阶段1: 准备数据（提取 Thing 属性）
   └── 阶段2: 存储并销毁物品
```

---

### 2.5 DigitalStorageGameComponent (全局管理器)
**文件位置**: `Source/Services/DigitalStorageGameComponent.cs`

**核心功能**:
1. **核心注册管理**
   - 维护全局核心列表
   - 按地图分组缓存，优化查找性能

2. **跨地图查找**
   - 优先本地图查找
   - 支持所有地图类型（包括车辆地图）
   - 使用反射兼容 Vehicle Map Framework

3. **虚拟物品掉落**
   - 核心拆除时分帧掉落虚拟物品
   - 每帧最多生成16个物品，避免卡顿

**关键方法**:
- `FindCoreWithItemType()`: 查找包含指定物品的核心
- `TryExtractItemFromAnyCoreGlobal()`: 从任何核心提取物品
- `QueueDropVirtualItems()`: 队列化掉落虚拟物品

---

## 三、Harmony 补丁系统

### 3.1 工作台制作补丁
**文件**: `Patch_WorkGiver_DoBill_TryFindBestBillIngredients.cs`

**功能**:
- 拦截工作台材料查找
- 优先使用预留物品
- 预留物品不足时从虚拟存储补货
- 支持跨地图制作（有芯片时）

**补货策略**:
- 本地图：补货到核心位置，让 pawn 走过去拿
- 跨地图：直接传送到工作台位置

---

### 3.2 建造材料补丁
**文件**: `Patch_WorkGiver_ConstructDeliverResources_V2.cs`

**功能**:
- 拦截建造材料查找
- 从虚拟存储提取材料
- 支持跨地图建造

---

### 3.3 资源统计补丁
**文件**: `Patch_ResourceCounter.cs`

**功能**:
- 将虚拟存储物品计入资源统计
- 支持工作台材料可用性检查

---

### 3.4 交易系统补丁
**文件**: `Patch_TradeUtility.cs`, `Patch_TradeAction.cs`

**功能**:
- 支持使用虚拟存储物品进行交易
- 兼容 Phinix 多人交易 mod
- 支持远行队交易

---

### 3.5 Mod 兼容性补丁

#### Pick Up And Haul
**文件**: `Patch_PickUpAndHaul_Compatibility.cs`
- 防止 pawn 重复搬运已数字化的物品

#### Achtung!
**文件**: `Patch_Achtung_Compatibility.cs`
- 兼容 Achtung! 的强制搬运功能

#### Phinix
**文件**: `Patch_Phinix_Compatibility.cs`
- 支持多人交易时使用虚拟存储

---

## 四、关键文件清单

### 4.1 核心组件 (Components/)
| 文件名 | 用途 |
|--------|------|
| Building_StorageCore.cs | 存储核心主逻辑 |
| Building_InputInterface.cs | 输入接口 |
| Building_DiskCabinet.cs | 磁盘柜（扩展容量） |
| CompStorageCoreUpgrade.cs | 核心升级组件 |
| CompVirtualIngredient.cs | 虚拟材料组件 |
| Hediff_TerminalImplant.cs | 终端芯片植入体 |
| CompUseEffect_TerminalImplant.cs | 芯片使用效果 |
| Dialog_RenameNetwork.cs | 网络重命名对话框 |
| DigitalStorageMapComponent.cs | 地图级管理器 |

### 4.2 数据层 (Data/)
| 文件名 | 用途 |
|--------|------|
| StoredItemData.cs | 虚拟物品数据结构 |

### 4.3 服务层 (Services/)
| 文件名 | 用途 |
|--------|------|
| AsyncItemConverter.cs | 异步物品转换器 |
| DigitalStorageGameComponent.cs | 全局管理器 |
| PawnStorageAccess.cs | Pawn 存储访问 |
| CaravanStorageAccess.cs | 远行队存储访问 |
| ObjectPool.cs | 对象池优化 |

### 4.4 UI 层 (UI/)
| 文件名 | 用途 |
|--------|------|
| Dialog_VirtualStorage.cs | 虚拟存储界面 |
| Dialog_ExtractAmount.cs | 提取数量对话框 |

### 4.5 设置层 (Settings/)
| 文件名 | 用途 |
|--------|------|
| DigitalStorageMod.cs | Mod 入口 |
| DigitalStorageSettings.cs | Mod 设置 |

### 4.6 线程层 (Threading/)
| 文件名 | 用途 |
|--------|------|
| JobScheduler.cs | 任务调度器 |
| MainThreadInvoker.cs | 主线程调用器 |

### 4.7 Harmony 补丁 (HarmonyPatches/)
| 文件名 | 用途 |
|--------|------|
| HarmonyInit.cs | Harmony 初始化 |
| Patch_WorkGiver_DoBill_TryFindBestBillIngredients.cs | 工作台制作 |
| Patch_WorkGiver_ConstructDeliverResources_V2.cs | 建造材料 |
| Patch_ResourceCounter.cs | 资源统计 |
| Patch_TradeUtility.cs | 交易系统 |
| Patch_TradeAction.cs | 交易动作 |
| Patch_Caravan_VirtualStorage.cs | 远行队存储 |
| Patch_CompRottable_Active.cs | 腐烂系统 |
| Patch_CompRottable_TicksUntilRotAtTemp.cs | 腐烂时间 |
| Patch_Thing_CanStackWith.cs | 物品堆叠 |
| Patch_Thing_TryAbsorbStack.cs | 物品合并 |
| Patch_Thing_Print.cs | 物品打印 |
| Patch_Thing_DrawGUIOverlay.cs | GUI 覆盖层 |
| Patch_StoreUtility_*.cs | 存储工具类补丁 |
| Patch_WealthWatcher.cs | 财富统计 |
| Patch_Pawn_JobTracker_StartJob_Teleport.cs | 任务传送 |
| Patch_PickUpAndHaul_Compatibility.cs | Pick Up And Haul 兼容 |
| Patch_Achtung_Compatibility.cs | Achtung! 兼容 |
| Patch_Phinix_Compatibility.cs | Phinix 兼容 |
| Patch_StackProtection.cs | 堆叠保护 |
| Patch_Designator_Build.cs | 建造指示器 |
| Patch_StaticConstructorOnStartup.cs | 静态构造器 |

### 4.8 定义文件 (Defs/)
| 文件名 | 用途 |
|--------|------|
| ThingDefs_Buildings.xml | 建筑定义（核心、接口） |
| ItemDefs_TerminalChip.xml | 终端芯片定义 |
| HediffDefs.xml | 植入体定义 |
| RecipeDefs_Surgery.xml | 手术配方 |
| RecipeDefs_Crafting.xml | 制作配方 |
| ResearchDefs.xml | 研究项目 |
| CategoryDefs.xml | 分类定义 |

### 4.9 本地化 (Languages/)
| 路径 | 用途 |
|------|------|
| English/Keyed/DigitalStorage.xml | 英文翻译 |
| ChineseSimplified/Keyed/DigitalStorage.xml | 简体中文翻译 |
| English/DefInjected/ | 英文定义注入 |

---

## 五、工作流程示例

### 5.1 物品存储流程
```
1. Pawn 搬运物品到输入接口
2. Building_InputInterface.Notify_ReceivedThing() 触发
3. 检查是否启用即时数字化
   ├── 是：直接调用 core.StoreItem()
   └── 否：传送到核心附近
4. Building_StorageCore.StoreItem()
   ├── 创建 StoredItemData
   ├── 尝试合并到已有堆叠
   └── 无法合并则创建新条目
5. 物品 DeSpawn 并 Destroy
```

### 5.2 工作台制作流程
```
1. WorkGiver_DoBill 查找材料
2. Patch_WorkGiver_DoBill_TryFindBestBillIngredients 拦截
3. 检查预留物品
   ├── 足够：使用预留物品
   └── 不足：从虚拟存储补货
4. 补货逻辑
   ├── 本地图：Spawn 到核心位置
   └── 跨地图：Spawn 到工作台位置
5. Pawn 拿取材料并制作
```

### 5.3 自动转换流程
```
1. Building_StorageCore.Tick() (每300 tick)
2. 统计物理物品数量
3. 检查是否超过预留数量
   └── 是：调用 AsyncItemConverter.StartAsyncConversion()
4. AsyncItemConverter.Update() (每帧)
   ├── 阶段1: 准备数据（提取 Thing 属性）
   └── 阶段2: 存储并销毁物品
5. 完成后清理状态
```

---

## 六、性能优化策略

### 6.1 分帧处理
- AsyncItemConverter 每帧处理15个物品
- 虚拟物品掉落每帧最多16个
- 避免单帧大量对象创建/销毁

### 6.2 对象池
- ItemData 对象池（容量64）
- Dictionary 复用，避免频繁分配
- 减少 GC 压力

### 6.3 缓存优化
- 核心按地图分组缓存
- 物品查找表（Dictionary）
- 优先本地图查找

### 6.4 延迟检查
- Tick 检查间隔：60/300 tick
- 避免每帧检查

---

## 七、配置选项

### 7.1 Mod 设置
| 选项 | 默认值 | 说明 |
|------|--------|------|
| costMultiplier | 1.0 | 造价倍率 |
| reservedCountPerItem | 100 | 每种物品预留数量 |
| enableDebugLog | false | 调试日志 |
| enableConversionLog | false | 转换日志 |
| countVirtualWealth | true | 虚拟物品计入财富 |
| interfaceInstantDigitize | true | 接口即时数字化 |

### 7.2 升级系统
| 等级 | 容量 | 耗电 | 升级材料 |
|------|------|------|----------|
| 1 | 100 | 100W | - |
| 2 | 250 | 150W | 钢铁x200, 零部件x10 |
| 3 | 500 | 200W | 玻璃钢x100, 零部件x20, 高级零部件x5 |
| 4 | 1000 | 300W | 玻璃钢x200, 高级零部件x15 |

---

## 八、已知限制与未来扩展

### 8.1 已知限制
- 仅支持部分物品类别（食物、原材料、纺织品等）
- 不支持装备、武器、家具等复杂物品
- 跨地图功能需要终端芯片

### 8.2 未来扩展方向
- 支持更多物品类别
- 网络互联（多核心共享存储）
- 无线访问点
- 自动化输入/输出
- 物品过滤器

---

## 九、技术亮点

1. **虚拟存储设计**：避免物理渲染，大幅降低性能开销
2. **预留物品系统**：完美兼容原版搬运系统
3. **分帧异步处理**：避免卡顿，提升用户体验
4. **对象池优化**：减少 GC 压力
5. **跨地图支持**：突破地图限制，提供远程访问
6. **Mod 兼容性**：主动适配多个流行 mod
7. **Harmony 补丁**：无侵入式修改游戏逻辑

---

## 十、开发建议

### 10.1 添加新功能时注意
- 保持虚拟存储和预留物品的一致性
- 使用分帧处理避免卡顿
- 考虑跨地图场景
- 添加调试日志开关

### 10.2 性能优化建议
- 避免每帧遍历大量物品
- 使用缓存减少重复计算
- 对象池复用临时对象
- 延迟非关键操作

### 10.3 兼容性建议
- 使用 Harmony Postfix 而非 Prefix
- 检查 null 和边界条件
- 提供软依赖检测
- 添加错误处理和回滚机制
