# 传送逻辑详解 - 重要补充文档

## ⚠️ 核心警告：双重触发机制

**这是本 mod 最关键的设计，修改时务必理解！**

本 mod 使用**两阶段传送策略**，在不同阶段处理不同类型的物品。混淆这两个阶段会导致严重的 bug（物品丢失、重复传送、卡顿等）。

---

## 一、两阶段传送机制概览

### 阶段1：WorkGiver 阶段（材料准备与补货）
- **触发时机**: WorkGiver 查找材料时
- **处理对象**: **虚拟存储中的物品**
- **主要操作**: 从虚拟存储提取物品并 Spawn 到地图上
- **芯片要求**: 跨地图需要芯片

### 阶段2：StartJob 阶段（预留物品传送）
- **触发时机**: Pawn 开始执行 Job 时
- **处理对象**: **预留物品（SlotGroup 中的物理物品）**
- **主要操作**: 将预留物品传送到工作位置
- **芯片要求**: **本地图也需要芯片**

---

## 二、传送逻辑对比表

| 场景 | WorkGiver 阶段 | StartJob 阶段 |
|------|---------------|---------------|
| **触发时机** | 查找材料时 | 开始执行 Job 时 |
| **芯片要求** | 跨地图需要 | 本地图也需要 |
| **处理对象** | 虚拟存储物品 | 预留物品（SlotGroup 中的物理物品） |
| **本地图工作台** | Spawn 到核心位置 | 传送到工作台位置 ✨ |
| **跨地图工作台** | Spawn 到工作台位置 ✨ | 不处理（已在 WorkGiver 处理） |
| **本地图建造** | Spawn 到核心位置 | 传送到 pawn 位置 ✨ |
| **跨地图建造** | Spawn 到蓝图位置 ✨ | 不处理（已在 WorkGiver 处理） |

---

## 三、详细实现分析

### 3.1 WorkGiver 阶段 - 工作台制作

**文件**: `Source/HarmonyPatches/Patch_WorkGiver_DoBill_TryFindBestBillIngredients.cs`

**关键代码**:
```csharp
// 第98-109行
if (source.isCrossMap) {
    // 跨地图：Spawn 到 pawn 所在地图的工作台位置
    replenishPos = billGiver.Position;
    spawnMap = pawn.Map;
} else {
    // 本地图：Spawn 到预留物品位置或核心位置
    replenishPos = source.reservedThing?.Position ?? source.core.Position;
    spawnMap = pawn.Map;
}

Thing finalThing = ReplenishToNeededCount(
    source.core, source.def, source.stuff, actualCount,
    replenishPos, spawnMap,
    source.isCrossMap ? null : source.reservedThing,  // 跨地图时忽略远程预留物品
    source.isCrossMap);
```

**补货逻辑**:
```csharp
// 第276-337行
private static Thing ReplenishToNeededCount(...) {
    // 跨地图：直接从虚拟存储提取全部数量
    if (isCrossMap) {
        Thing extracted = core.ExtractItem(def, needed, stuff);
        GenSpawn.Spawn(extracted, position, map, WipeMode.Vanish);
        return extracted;
    }

    // 本地图：原有逻辑（预留物品 + 补货）
    int currentCount = existingReserved?.stackCount ?? 0;
    int needMore = needed - currentCount;

    if (needMore > 0) {
        Thing extractedLocal = core.ExtractItem(def, needMore, stuff);
        // 合并到预留物品或 Spawn 到核心位置
    }
}
```

---

### 3.2 WorkGiver 阶段 - 建造材料

**文件**: `Source/HarmonyPatches/Patch_WorkGiver_ConstructDeliverResources_V2.cs`

**关键代码**:
```csharp
// 第142-168行
if (isCrossMap) {
    // ===== 跨地图逻辑 =====
    // 只查虚拟存储（远程核心的预留物品在另一张地图上，本地 pawn 拿不到）
    int virtualCount = core.GetVirtualItemCount(def);

    if (virtualCount >= needed) {
        Thing extracted = core.ExtractItem(def, needed, null);
        // Spawn 到 pawn 所在地图的蓝图位置
        GenSpawn.Spawn(extracted, blueprintPos, pawnMap, WipeMode.Vanish);
        return extracted;
    }
} else {
    // ===== 本地图逻辑（原有逻辑不变） =====
    Thing reservedThing = core.FindReservedItem(def, null);
    int reservedCount = reservedThing?.stackCount ?? 0;
    int virtualCount = core.GetVirtualItemCount(def);

    // 预留足够：从预留分出
    // 预留不够：补货后合并
    // 最终 Spawn 到核心位置
}
```

**注释说明**:
```csharp
// 第19行
/// 注意：这里只补货，不传送。传送在 StartJob 阶段处理（有芯片时）
```

---

### 3.3 StartJob 阶段 - 任务传送

**文件**: `Source/HarmonyPatches/Patch_Pawn_JobTracker_StartJob_Teleport.cs`

**文件头注释**:
```csharp
// 第1-2行
// Job启动时的传送逻辑
// DoBill/HaulToContainer: 有芯片时传送材料到工作位置
```

**处理 DoBill（工作台制作）**:
```csharp
// 第43-122行
private static void HandleDoBill(Job job, Pawn pawn) {
    bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
    if (!hasChip) {
        return; // 无芯片不传送
    }

    // 遍历 Job 的材料列表
    for (int i = 0; i < job.targetQueueB.Count; i++) {
        Thing material = targetInfo.Thing;

        // 检查材料是否在核心的SlotGroup中（预留物品）
        Building_StorageCore materialCore = FindCoreWithItem(material, pawn.Map);
        if (materialCore != null) {
            // 传送到工作台位置
            IntVec3 workbenchPos = GetBillGiverRootCell(billGiver, pawn);

            if (material.stackCount <= countNeeded) {
                // 整个物品传送
                material.DeSpawn(0);
                GenSpawn.Spawn(material, workbenchPos, pawn.Map, WipeMode.Vanish);
            } else {
                // 分离需要的数量后传送
                Thing split = material.SplitOff(countNeeded);
                GenSpawn.Spawn(split, workbenchPos, pawn.Map, WipeMode.Vanish);
            }

            FleckMaker.ThrowLightningGlow(thingToTeleport.DrawPos, thingToTeleport.Map, 0.5f);
        }
    }
}
```

**处理 HaulToContainer（建造搬运）**:
```csharp
// 第138-194行
private static void HandleHaulToContainer(Job job, Pawn pawn) {
    bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
    if (!hasChip) {
        return; // 无芯片不传送
    }

    Thing material = job.targetA.Thing;

    // 检查材料是否在核心的SlotGroup中（预留物品）
    Building_StorageCore materialCore = FindCoreWithItem(material, pawn.Map);

    if (materialCore != null) {
        // 传送到pawn脚底
        thingToTeleport.DeSpawn(0);
        GenSpawn.Spawn(thingToTeleport, pawn.Position, pawn.Map, WipeMode.Vanish);
        FleckMaker.ThrowLightningGlow(thingToTeleport.DrawPos, thingToTeleport.Map, 0.5f);

        // 更新Job的target
        job.targetA = thingToTeleport;
    }
}
```

**FindCoreWithItem 方法**:
```csharp
// 第196-232行
private static Building_StorageCore FindCoreWithItem(Thing item, Map map) {
    foreach (Building_StorageCore core in gameComp.GetAllCores()) {
        if (core.Map != map) continue; // 只查本地图

        SlotGroup slotGroup = core.GetSlotGroup();
        if (slotGroup != null) {
            // 方法1：检查 HeldThings
            foreach (Thing thing in slotGroup.HeldThings) {
                if (thing == item) return core;
            }

            // 方法2：检查物品位置是否在 SlotGroup 的格子范围内
            if (slotGroup.CellsList.Contains(item.Position)) {
                return core;
            }
        }
    }
    return null;
}
```

---

## 四、完整工作流程示例

### 场景A：本地图工作台制作（有芯片）

```
1. WorkGiver 阶段 (Patch_WorkGiver_DoBill_TryFindBestBillIngredients)
   ├── 检查预留物品 → 数量不足
   ├── 从虚拟存储提取 → core.ExtractItem(def, needMore, stuff)
   ├── Spawn 到核心位置 → GenSpawn.Spawn(extracted, core.Position, pawnMap)
   └── 创建 Job → Job.targetQueueB 包含核心位置的材料

2. StartJob 阶段 (Patch_Pawn_JobTracker_StartJob_Teleport)
   ├── Pawn 开始执行 DoBill Job
   ├── 检查芯片 → hasChip = true
   ├── 遍历 Job.targetQueueB 中的材料
   ├── FindCoreWithItem(material, pawn.Map) → 找到核心
   ├── 传送到工作台 → GenSpawn.Spawn(material, workbenchPos, pawn.Map)
   └── Pawn 直接在工作台旁边拿取 ✨
```

### 场景B：本地图工作台制作（无芯片）

```
1. WorkGiver 阶段
   ├── 检查预留物品 → 数量不足
   ├── 从虚拟存储提取
   ├── Spawn 到核心位置
   └── 创建 Job

2. StartJob 阶段
   ├── Pawn 开始执行 DoBill Job
   ├── 检查芯片 → hasChip = false
   ├── 跳过传送 → return
   └── Pawn 走到核心位置拿取材料（原版行为）
```

### 场景C：跨地图工作台制作（有芯片）

```
1. WorkGiver 阶段
   ├── 检测到跨地图 → isCrossMap = true
   ├── 检查芯片 → hasChip = true
   ├── 从远程核心虚拟存储提取 → core.ExtractItem(def, needed, stuff)
   ├── 直接 Spawn 到工作台位置 → GenSpawn.Spawn(extracted, billGiver.Position, pawn.Map)
   └── 创建 Job → 材料已在工作台位置 ✨

2. StartJob 阶段
   ├── Pawn 开始执行 DoBill Job
   ├── FindCoreWithItem(material, pawn.Map) → 找不到（材料不在核心 SlotGroup 中）
   └── 跳过传送 → 材料已在正确位置
```

### 场景D：本地图建造（有芯片）

```
1. WorkGiver 阶段 (Patch_WorkGiver_ConstructDeliverResources_V2)
   ├── 检查预留物品 → 数量不足
   ├── 从虚拟存储提取
   ├── Spawn 到核心位置
   └── 创建 HaulToContainer Job

2. StartJob 阶段
   ├── Pawn 开始执行 HaulToContainer Job
   ├── 检查芯片 → hasChip = true
   ├── FindCoreWithItem(material, pawn.Map) → 找到核心
   ├── 传送到 pawn 脚底 → GenSpawn.Spawn(material, pawn.Position, pawn.Map)
   └── Pawn 直接拾取 ✨
```

### 场景E：跨地图建造（有芯片）

```
1. WorkGiver 阶段
   ├── 检测到跨地图 → isCrossMap = true
   ├── 检查芯片 → hasChip = true
   ├── 从远程核心虚拟存储提取
   ├── 直接 Spawn 到蓝图位置 → GenSpawn.Spawn(extracted, blueprintPos, pawn.Map)
   └── 创建 HaulToContainer Job ✨

2. StartJob 阶段
   ├── Pawn 开始执行 HaulToContainer Job
   ├── FindCoreWithItem(material, pawn.Map) → 找不到
   └── 跳过传送 → 材料已在蓝图位置
```

---

## 五、设计优势

1. **分层处理**
   - WorkGiver 负责补货（虚拟 → 物理）
   - StartJob 负责传送（物理 → 工作位置）
   - 职责清晰，不会混淆

2. **性能优化**
   - 跨地图直接传送，避免 pawn 路径计算
   - 本地图有芯片时也传送，提升效率

3. **兼容性好**
   - 本地图无芯片时走原版逻辑
   - 不破坏原版 Job 系统

4. **用户体验**
   - 有芯片时材料直接出现在需要的位置
   - 无芯片时保持原版行为

---

## 六、⚠️ 开发注意事项

### 6.1 不要混淆两个阶段

**错误示例 1：在 WorkGiver 阶段传送预留物品**
```csharp
// ❌ 错误！预留物品应该在 StartJob 阶段传送
if (reservedThing != null) {
    GenSpawn.Spawn(reservedThing, workbenchPos, map);
}
```

**正确做法**:
```csharp
// ✅ WorkGiver 只处理虚拟存储
Thing extracted = core.ExtractItem(def, needMore, stuff);
GenSpawn.Spawn(extracted, core.Position, map); // Spawn 到核心位置，等待 StartJob
```

---

**错误示例 2：在 StartJob 阶段处理虚拟存储**
```csharp
// ❌ 错误！虚拟存储应该在 WorkGiver 阶段处理
Thing extracted = core.ExtractItem(def, count);
GenSpawn.Spawn(extracted, workbenchPos, map);
```

**正确做法**:
```csharp
// ✅ StartJob 只处理预留物品（SlotGroup 中的物理物品）
Building_StorageCore materialCore = FindCoreWithItem(material, pawn.Map);
if (materialCore != null && hasChip) {
    GenSpawn.Spawn(material, workbenchPos, pawn.Map);
}
```

---

### 6.2 不要重复传送

**错误示例：跨地图物品在 StartJob 再次传送**
```csharp
// ❌ 错误！跨地图物品已在 WorkGiver 阶段传送
if (isCrossMap && hasChip) {
    GenSpawn.Spawn(material, workbenchPos, map); // 重复传送！
}
```

**正确做法**:
```csharp
// ✅ StartJob 只处理本地图预留物品
Building_StorageCore materialCore = FindCoreWithItem(material, pawn.Map);
// FindCoreWithItem 只查本地图，跨地图物品找不到，自动跳过
```

---

### 6.3 芯片检查的位置

**WorkGiver 阶段**:
```csharp
// 跨地图需要芯片
if (isCrossMap && !hasChip) {
    continue; // 跳过这个核心
}
```

**StartJob 阶段**:
```csharp
// 本地图传送也需要芯片
bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
if (!hasChip) {
    return; // 不传送，走原版逻辑
}
```

---

### 6.4 Spawn 位置的选择

**WorkGiver 阶段**:
```csharp
if (isCrossMap) {
    // 跨地图：直接传送到目标位置
    GenSpawn.Spawn(extracted, targetPos, pawnMap);
} else {
    // 本地图：Spawn 到核心位置，等待 StartJob 传送
    GenSpawn.Spawn(extracted, core.Position, pawnMap);
}
```

**StartJob 阶段**:
```csharp
if (job.def == JobDefOf.DoBill) {
    // 工作台制作：传送到工作台位置
    GenSpawn.Spawn(material, workbenchPos, pawn.Map);
} else if (job.def == JobDefOf.HaulToContainer) {
    // 建造搬运：传送到 pawn 位置
    GenSpawn.Spawn(material, pawn.Position, pawn.Map);
}
```

---

### 6.5 预留物品的作用

1. **兼容原版搬运系统**
   - 原版 AI 只认识物理物品
   - 预留物品让原版系统能"看到"材料

2. **在 StartJob 阶段被传送**
   - 有芯片时传送到工作位置
   - 无芯片时 pawn 走过去拿

3. **不在 WorkGiver 阶段处理**
   - WorkGiver 只处理虚拟存储
   - 预留物品已经是物理物品，不需要补货

---

## 七、调试技巧

### 7.1 启用调试日志

在 Mod 设置中启用：
- `enableDebugLog`: 基础调试日志
- `enableConversionLog`: 转换日志

### 7.2 关键日志位置

**WorkGiver 阶段**:
```csharp
if (DigitalStorageSettings.enableDebugLog)
    Log.Message($"[DoBill] 补货成功: {finalThing.Label} x{finalThing.stackCount} at {finalThing.Position}");
```

**StartJob 阶段**:
```csharp
if (DigitalStorageSettings.enableDebugLog)
    Log.Message($"[数字存储] DoBill传送材料: {thingToTeleport.Label} x{thingToTeleport.stackCount} 到工作台 {workbenchPos}");
```

### 7.3 检查清单

遇到传送问题时，按顺序检查：

1. **芯片检查**
   - Pawn 是否有终端芯片？
   - `PawnStorageAccess.HasTerminalImplant(pawn)`

2. **物品位置**
   - 物品在虚拟存储还是预留物品？
   - `core.GetVirtualItemCount(def)` vs `core.FindReservedItem(def)`

3. **地图检查**
   - 是本地图还是跨地图？
   - `core.Map == pawn.Map`

4. **阶段检查**
   - 当前在 WorkGiver 还是 StartJob？
   - 查看调用栈

5. **Spawn 位置**
   - 物品 Spawn 到哪里了？
   - 检查 `thing.Position` 和 `thing.Map`

---

## 八、常见 Bug 及解决方案

### Bug 1：物品丢失

**症状**: 材料从虚拟存储消失，但没有出现在地图上

**原因**:
- WorkGiver 提取了虚拟物品但没有 Spawn
- StartJob 传送时物品已被销毁

**解决**:
```csharp
// 确保提取后立即 Spawn
Thing extracted = core.ExtractItem(def, count, stuff);
if (extracted != null) {
    GenSpawn.Spawn(extracted, position, map, WipeMode.Vanish);
}
```

---

### Bug 2：重复传送

**症状**: 物品被传送两次，导致数量翻倍或位置错误

**原因**:
- 跨地图物品在 WorkGiver 和 StartJob 都被传送
- 预留物品在 WorkGiver 阶段被错误处理

**解决**:
```csharp
// StartJob 只处理本地图预留物品
Building_StorageCore materialCore = FindCoreWithItem(material, pawn.Map);
// FindCoreWithItem 只查本地图，跨地图物品自动跳过
```

---

### Bug 3：无芯片时无法使用材料

**症状**: 有预留物品但 pawn 不去拿

**原因**:
- StartJob 阶段错误地要求芯片
- 预留物品位置不正确

**解决**:
```csharp
// 无芯片时应该跳过传送，让原版处理
bool hasChip = PawnStorageAccess.HasTerminalImplant(pawn);
if (!hasChip) {
    return; // 不传送，pawn 会走到核心位置拿取
}
```

---

### Bug 4：跨地图无法使用材料

**症状**: 有芯片但跨地图制作失败

**原因**:
- WorkGiver 阶段没有检查芯片
- 跨地图物品 Spawn 位置错误

**解决**:
```csharp
// 跨地图必须检查芯片
if (isCrossMap && !hasChip) {
    continue; // 跳过这个核心
}

// 跨地图直接 Spawn 到目标位置
if (isCrossMap) {
    GenSpawn.Spawn(extracted, targetPos, pawnMap);
}
```

---

## 九、总结

### 核心原则

1. **WorkGiver 处理虚拟存储，StartJob 处理预留物品**
2. **跨地图在 WorkGiver 直接传送，本地图在 StartJob 传送**
3. **芯片是传送的必要条件，无芯片走原版逻辑**
4. **不要重复传送，不要混淆阶段**

### 修改建议

- 修改传送逻辑前，先理解两阶段机制
- 添加新功能时，确定在哪个阶段处理
- 测试时覆盖所有场景（本地图/跨地图，有芯片/无芯片）
- 启用调试日志，追踪物品流向

### 参考文件

- `Patch_WorkGiver_DoBill_TryFindBestBillIngredients.cs`
- `Patch_WorkGiver_ConstructDeliverResources_V2.cs`
- `Patch_Pawn_JobTracker_StartJob_Teleport.cs`

---

**最后提醒**: 这个双重触发机制是整个 mod 的核心，任何修改都可能影响用户体验。修改前请务必理解原理，修改后请全面测试！
