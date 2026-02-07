// Phinix & PhinixRedPacket 兼容补丁
// 让 Phinix 交易和红包系统能看到并消费虚拟存储中的物品
//
// 原理：
// Phinix 和 RedPacket 都通过 haulDestinationManager.AllGroups → HeldThings 获取可交易物品。
// 虚拟存储中的物品不在任何 SlotGroup 中，所以它们看不到。
//
// 方案：
// 1. Patch StackedThings.GroupThings（Phinix 的物品分组方法），注入虚拟存储物品
// 2. 追踪虚拟 Thing 实例，在 DeSpawn/Destroy 时从虚拟存储扣除

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DigitalStorage.Components;
using DigitalStorage.Data;
using DigitalStorage.Services;
using DigitalStorage.Settings;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// Phinix 兼容：追踪为 Phinix 创建的虚拟 Thing，在它们被消费时从虚拟存储扣除
    /// </summary>
    public static class PhinixCompatibility
    {
        /// <summary>
        /// 追踪虚拟 Thing → 源核心的映射
        /// </summary>
        private static readonly Dictionary<int, VirtualThingInfo> _virtualThings = new Dictionary<int, VirtualThingInfo>();
        private static readonly object _lock = new object();

        public class VirtualThingInfo
        {
            public Building_StorageCore sourceCore;
            public ThingDef def;
            public ThingDef stuffDef;
            public int originalStackCount;
        }

        /// <summary>
        /// 注册一个虚拟 Thing（用于追踪）
        /// </summary>
        public static void RegisterVirtualThing(Thing thing, Building_StorageCore core)
        {
            if (thing == null || core == null) return;

            lock (_lock)
            {
                _virtualThings[thing.thingIDNumber] = new VirtualThingInfo
                {
                    sourceCore = core,
                    def = thing.def,
                    stuffDef = thing.Stuff,
                    originalStackCount = thing.stackCount
                };
            }
        }

        /// <summary>
        /// 检查并消费虚拟 Thing（在 DeSpawn 或 Destroy 时调用）
        /// </summary>
        public static bool TryConsumeVirtualThing(Thing thing)
        {
            if (thing == null) return false;

            VirtualThingInfo info;
            lock (_lock)
            {
                if (!_virtualThings.TryGetValue(thing.thingIDNumber, out info))
                {
                    return false;
                }
                _virtualThings.Remove(thing.thingIDNumber);
            }

            if (info.sourceCore == null || !info.sourceCore.Spawned || !info.sourceCore.Powered)
            {
                return false;
            }

            // 计算实际消费的数量（可能被 SplitOff 过）
            int consumed = info.originalStackCount;

            // 从虚拟存储扣除
            int deducted = info.sourceCore.DeductVirtualItems(info.def, consumed);

            if (DigitalStorageSettings.enableDebugLog)
            {
                Log.Message($"[DigitalStorage] Phinix compat: consumed virtual thing {info.def.label} x{consumed}, deducted={deducted}");
            }

            return true;
        }

        /// <summary>
        /// 检查 Thing 是否是我们追踪的虚拟物品
        /// </summary>
        public static bool IsVirtualThing(Thing thing)
        {
            if (thing == null) return false;
            lock (_lock)
            {
                return _virtualThings.ContainsKey(thing.thingIDNumber);
            }
        }

        /// <summary>
        /// 清理所有追踪（安全措施）
        /// </summary>
        public static void ClearTracking()
        {
            lock (_lock)
            {
                _virtualThings.Clear();
            }
        }

        /// <summary>
        /// 获取所有核心的虚拟物品作为临时 Thing 列表（用于注入到 Phinix 的物品列表）
        /// </summary>
        public static List<Thing> GetVirtualThingsForPhinix()
        {
            List<Thing> result = new List<Thing>();

            DigitalStorageGameComponent gameComp = Current.Game?.GetComponent<DigitalStorageGameComponent>();
            if (gameComp == null) return result;

            foreach (Building_StorageCore core in gameComp.GetAllCores())
            {
                if (core == null || !core.Spawned || !core.Powered) continue;

                foreach (StoredItemData itemData in core.GetAllStoredItems())
                {
                    if (itemData == null || itemData.def == null || itemData.stackCount <= 0) continue;

                    Thing virtualThing = itemData.CreateThing();
                    if (virtualThing != null)
                    {
                        RegisterVirtualThing(virtualThing, core);
                        result.Add(virtualThing);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 尝试应用 Phinix 兼容补丁（软依赖，Phinix 不存在时不报错）
        /// </summary>
        public static void TryApplyPatches(Harmony harmony)
        {
            try
            {
                // 检查 Phinix 是否加载
                Assembly phinixAssembly = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.GetName().Name == "Phinix")
                    {
                        phinixAssembly = asm;
                        break;
                    }
                }

                if (phinixAssembly == null)
                {
                    return; // Phinix 未加载，跳过
                }

                // 找到 StackedThings.GroupThings 方法
                Type stackedThingsType = phinixAssembly.GetType("PhinixClient.StackedThings");
                if (stackedThingsType == null)
                {
                    Log.Warning("[DigitalStorage] Phinix found but StackedThings type not found");
                    return;
                }

                MethodInfo groupThingsMethod = stackedThingsType.GetMethod("GroupThings",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(IEnumerable<Thing>) },
                    null);

                if (groupThingsMethod == null)
                {
                    Log.Warning("[DigitalStorage] Phinix StackedThings.GroupThings method not found");
                    return;
                }

                // Patch GroupThings 的参数，在调用前注入虚拟物品
                MethodInfo prefixMethod = typeof(PhinixCompatibility).GetMethod(nameof(GroupThingsPrefix),
                    BindingFlags.Public | BindingFlags.Static);

                harmony.Patch(groupThingsMethod, prefix: new HarmonyMethod(prefixMethod));

                Log.Message("[DigitalStorage] Phinix compatibility patches applied successfully");
            }
            catch (Exception ex)
            {
                Log.Warning($"[DigitalStorage] Failed to apply Phinix compatibility patches: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix for StackedThings.GroupThings - 注入虚拟存储物品到输入列表
        /// </summary>
        public static void GroupThingsPrefix(ref IEnumerable<Thing> things)
        {
            if (things == null) return;

            try
            {
                // 清理之前的追踪（新一轮刷新）
                ClearTracking();

                List<Thing> virtualThings = GetVirtualThingsForPhinix();
                if (virtualThings.Count > 0)
                {
                    things = things.Concat(virtualThings);

                    if (DigitalStorageSettings.enableDebugLog)
                    {
                        Log.Message($"[DigitalStorage] Phinix compat: injected {virtualThings.Count} virtual item types");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[DigitalStorage] Phinix GroupThings prefix error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Patch Thing.DeSpawn：当 Phinix 对虚拟 Thing 调用 DeSpawn 时，从虚拟存储扣除
    /// </summary>
    [HarmonyPatch(typeof(Thing), "DeSpawn")]
    public static class Patch_Thing_DeSpawn_PhinixCompat
    {
        public static bool Prefix(Thing __instance)
        {
            // 虚拟 Thing 没有 Spawn，DeSpawn 会报错，需要拦截
            if (PhinixCompatibility.IsVirtualThing(__instance) && !__instance.Spawned)
            {
                // 从虚拟存储扣除
                PhinixCompatibility.TryConsumeVirtualThing(__instance);
                return false; // 跳过原版 DeSpawn（因为没有 Spawn 过）
            }
            return true;
        }
    }
}
