// Phinix & PhinixRedPacket 兼容补丁
//
// 策略：
// 不 patch 通用的 GroupThings（会污染报价缓存），
// 而是分别 Postfix TradeWindow.PreOpen 和 RedPacketTab.RefreshAvailableItems，
// 通过反射直接向 availableItems 字段注入虚拟物品。

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
    public static class PhinixCompatibility
    {
        internal static readonly Dictionary<int, VirtualThingInfo> _virtualThings = new Dictionary<int, VirtualThingInfo>();
        internal static readonly object _lock = new object();
        private static bool _patchApplied = false;

        // 缓存反射结果
        private static MethodInfo _groupThingsMethod;
        private static FieldInfo _tradeWindow_availableItems;
        private static FieldInfo _tradeWindow_filteredAvailableItems;
        private static FieldInfo _redPacketTab_availableItems;
        private static FieldInfo _redPacketTab_filteredItems;

        public class VirtualThingInfo
        {
            public Building_StorageCore sourceCore;
            public ThingDef def;
        }

        public static void RegisterVirtualThing(Thing thing, Building_StorageCore core)
        {
            if (thing == null || core == null) return;
            lock (_lock)
            {
                _virtualThings[thing.thingIDNumber] = new VirtualThingInfo
                {
                    sourceCore = core,
                    def = thing.def
                };
            }
        }

        public static bool IsVirtualThing(Thing thing)
        {
            if (thing == null) return false;
            lock (_lock)
            {
                return _virtualThings.ContainsKey(thing.thingIDNumber);
            }
        }

        public static void DeductAndUntrack(Thing thing)
        {
            if (thing == null) return;

            VirtualThingInfo info;
            lock (_lock)
            {
                if (!_virtualThings.TryGetValue(thing.thingIDNumber, out info))
                    return;
                _virtualThings.Remove(thing.thingIDNumber);
            }

            if (info.sourceCore != null && info.sourceCore.Spawned && info.sourceCore.Powered)
            {
                int deducted = info.sourceCore.DeductVirtualItems(info.def, thing.stackCount);
                Log.Message($"[DigitalStorage][PH] Deducted: {info.def?.label} x{thing.stackCount}, actual={deducted}");
            }
            else
            {
                Log.Warning($"[DigitalStorage][PH] Deduct FAILED - core unavailable for {info.def?.label}");
            }
        }

        /// <summary>
        /// 创建虚拟物品的 Thing 列表并注册追踪
        /// </summary>
        public static List<Thing> CreateVirtualThings()
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

                    try
                    {
                        Thing virtualThing = itemData.CreateThing();
                        if (virtualThing != null)
                        {
                            RegisterVirtualThing(virtualThing, core);
                            result.Add(virtualThing);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[DigitalStorage][PH] CreateThing error: {ex.Message}");
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 将虚拟物品注入到目标实例的 availableItems 字段中
        /// </summary>
        private static void InjectVirtualItems(object targetInstance, FieldInfo availableItemsField, FieldInfo filteredItemsField)
        {
            if (targetInstance == null || availableItemsField == null) return;

            try
            {
                List<Thing> virtualThings = CreateVirtualThings();
                if (virtualThings.Count == 0) return;

                if (_groupThingsMethod == null)
                {
                    Log.Warning("[DigitalStorage][PH] GroupThings method not cached");
                    return;
                }

                // 过滤：只要 Item 类别且非尸体
                IEnumerable<Thing> filtered = virtualThings.Where(
                    t => t.def.category == ThingCategory.Item && !t.def.IsCorpse);

                // 调用 StackedThings.GroupThings 分组
                object grouped = _groupThingsMethod.Invoke(null, new object[] { filtered });
                if (grouped == null) return;

                // 获取当前 availableItems 并追加
                object currentItems = availableItemsField.GetValue(targetInstance);
                if (currentItems == null) return;

                MethodInfo addRangeMethod = currentItems.GetType().GetMethod("AddRange");
                if (addRangeMethod != null)
                {
                    addRangeMethod.Invoke(currentItems, new object[] { grouped });
                }

                // 更新 filteredItems 使物品立即可见
                if (filteredItemsField != null)
                {
                    // 直接设为同一个列表引用（和 Phinix 原版 PreOpen 行为一致）
                    filteredItemsField.SetValue(targetInstance, currentItems);
                }

                Log.Message($"[DigitalStorage][PH] Injected {virtualThings.Count} virtual items into {targetInstance.GetType().Name}");
            }
            catch (Exception ex)
            {
                Log.Warning($"[DigitalStorage][PH] InjectVirtualItems error: {ex}");
            }
        }

        // ========== Patch 入口 ==========

        public static void TryApplyPatches(Harmony harmony)
        {
            if (_patchApplied) return;

            try
            {
                // 查找 Phinix 程序集
                Assembly phinixAssembly = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string asmName = asm.GetName().Name;
                    if (asmName == "PhinixClient" || asmName == "Phinix")
                    {
                        phinixAssembly = asm;
                        Log.Message($"[DigitalStorage][PH] Found Phinix assembly: {asmName}");
                        break;
                    }
                }

                if (phinixAssembly == null) return;

                // 缓存 StackedThings.GroupThings
                Type stackedThingsType = phinixAssembly.GetType("PhinixClient.StackedThings");
                if (stackedThingsType != null)
                {
                    _groupThingsMethod = stackedThingsType.GetMethod("GroupThings",
                        BindingFlags.Public | BindingFlags.Static,
                        null, new Type[] { typeof(IEnumerable<Thing>) }, null);
                }

                if (_groupThingsMethod == null)
                {
                    Log.Warning("[DigitalStorage][PH] StackedThings.GroupThings not found, aborting");
                    return;
                }

                // ===== Patch TradeWindow.PreOpen =====
                Type tradeWindowType = phinixAssembly.GetType("PhinixClient.TradeWindow");
                if (tradeWindowType != null)
                {
                    _tradeWindow_availableItems = tradeWindowType.GetField("availableItems",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    _tradeWindow_filteredAvailableItems = tradeWindowType.GetField("filteredAvailableItems",
                        BindingFlags.NonPublic | BindingFlags.Instance);

                    MethodInfo preOpenMethod = tradeWindowType.GetMethod("PreOpen",
                        BindingFlags.Public | BindingFlags.Instance);

                    if (preOpenMethod != null && _tradeWindow_availableItems != null)
                    {
                        harmony.Patch(preOpenMethod,
                            postfix: new HarmonyMethod(typeof(PhinixCompatibility).GetMethod(
                                nameof(TradeWindowPreOpenPostfix), BindingFlags.Public | BindingFlags.Static)));
                        Log.Message("[DigitalStorage][PH] Patched TradeWindow.PreOpen");
                    }
                }

                // ===== Patch PopSelected / DeleteSelected / GetSelectedThingsAsProto =====
                if (stackedThingsType != null)
                {
                    MethodInfo popSelectedMethod = stackedThingsType.GetMethod("PopSelected",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (popSelectedMethod != null)
                    {
                        harmony.Patch(popSelectedMethod,
                            postfix: new HarmonyMethod(typeof(PhinixCompatibility).GetMethod(
                                nameof(PopSelectedPostfix), BindingFlags.Public | BindingFlags.Static)));
                        Log.Message("[DigitalStorage][PH] Patched PopSelected");
                    }

                    MethodInfo deleteSelectedMethod = stackedThingsType.GetMethod("DeleteSelected",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (deleteSelectedMethod != null)
                    {
                        harmony.Patch(deleteSelectedMethod,
                            prefix: new HarmonyMethod(typeof(PhinixCompatibility).GetMethod(
                                nameof(DeleteSelectedPrefix), BindingFlags.Public | BindingFlags.Static)));
                        Log.Message("[DigitalStorage][PH] Patched DeleteSelected");
                    }

                    MethodInfo getSelectedAsProtoMethod = stackedThingsType.GetMethod("GetSelectedThingsAsProto",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (getSelectedAsProtoMethod != null)
                    {
                        harmony.Patch(getSelectedAsProtoMethod,
                            postfix: new HarmonyMethod(typeof(PhinixCompatibility).GetMethod(
                                nameof(GetSelectedAsProtoPostfix), BindingFlags.Public | BindingFlags.Static)));
                        Log.Message("[DigitalStorage][PH] Patched GetSelectedThingsAsProto");
                    }
                }

                // ===== Patch RedPacketTab.RefreshAvailableItems =====
                TryPatchRedPacket(harmony);

                _patchApplied = true;
                Log.Message("[DigitalStorage][PH] All Phinix compatibility patches applied");
            }
            catch (Exception ex)
            {
                Log.Warning($"[DigitalStorage][PH] TryApplyPatches FAILED: {ex}");
            }
        }

        private static void TryPatchRedPacket(Harmony harmony)
        {
            try
            {
                Assembly redPacketAssembly = null;
                foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string asmName = asm.GetName().Name;
                    if (asmName == "PhinixRedPacket" || asmName.IndexOf("RedPacket", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        redPacketAssembly = asm;
                        Log.Message($"[DigitalStorage][PH] Found RedPacket assembly: {asmName}");
                        break;
                    }
                }

                if (redPacketAssembly == null) return;

                Type redPacketTabType = redPacketAssembly.GetType("PhinixRedPacket.RedPacketTab");
                if (redPacketTabType == null)
                {
                    Log.Warning("[DigitalStorage][PH] RedPacketTab type not found");
                    return;
                }

                _redPacketTab_availableItems = redPacketTabType.GetField("availableItems",
                    BindingFlags.NonPublic | BindingFlags.Instance);
                _redPacketTab_filteredItems = redPacketTabType.GetField("filteredItems",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                MethodInfo refreshMethod = redPacketTabType.GetMethod("RefreshAvailableItems",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (refreshMethod != null && _redPacketTab_availableItems != null)
                {
                    harmony.Patch(refreshMethod,
                        postfix: new HarmonyMethod(typeof(PhinixCompatibility).GetMethod(
                            nameof(RedPacketRefreshPostfix), BindingFlags.Public | BindingFlags.Static)));
                    Log.Message("[DigitalStorage][PH] Patched RedPacketTab.RefreshAvailableItems");
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[DigitalStorage][PH] TryPatchRedPacket error: {ex.Message}");
            }
        }

        // ========== Harmony Postfix/Prefix ==========

        public static void TradeWindowPreOpenPostfix(object __instance)
        {
            Log.Message("[DigitalStorage][PH] TradeWindow.PreOpen postfix triggered");
            InjectVirtualItems(__instance, _tradeWindow_availableItems, _tradeWindow_filteredAvailableItems);
        }

        public static void RedPacketRefreshPostfix(object __instance)
        {
            Log.Message("[DigitalStorage][PH] RedPacketTab.RefreshAvailableItems postfix triggered");
            InjectVirtualItems(__instance, _redPacketTab_availableItems, _redPacketTab_filteredItems);
        }

        public static void PopSelectedPostfix(object __instance, IEnumerable<Thing> __result)
        {
            if (__result == null) return;

            try
            {
                foreach (Thing thing in __result)
                {
                    if (thing != null && IsVirtualThing(thing))
                    {
                        Log.Message($"[DigitalStorage][PH] PopSelected: deducting {thing.def?.defName} x{thing.stackCount} (id={thing.thingIDNumber})");
                        DeductAndUntrack(thing);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[DigitalStorage][PH] PopSelectedPostfix error: {ex.Message}");
            }
        }

        public static void DeleteSelectedPrefix(object __instance)
        {
            try
            {
                Type type = __instance.GetType();
                FieldInfo thingsField = type.GetField("Things", BindingFlags.Public | BindingFlags.Instance);
                FieldInfo selectedField = type.GetField("Selected", BindingFlags.Public | BindingFlags.Instance);

                if (thingsField == null || selectedField == null) return;

                List<Thing> things = thingsField.GetValue(__instance) as List<Thing>;
                int selected = (int)selectedField.GetValue(__instance);

                if (things == null || selected <= 0) return;

                int remaining = selected;
                foreach (Thing thing in things)
                {
                    if (remaining <= 0) break;
                    if (thing == null || !IsVirtualThing(thing)) continue;

                    int take = System.Math.Min(thing.stackCount, remaining);
                    if (take > 0)
                    {
                        VirtualThingInfo info;
                        lock (_lock)
                        {
                            if (_virtualThings.TryGetValue(thing.thingIDNumber, out info))
                            {
                                if (info.sourceCore != null && info.sourceCore.Spawned && info.sourceCore.Powered)
                                {
                                    int deducted = info.sourceCore.DeductVirtualItems(info.def, take);
                                    Log.Message($"[DigitalStorage][PH] DeleteSelected: deducted {info.def?.label} x{take}, actual={deducted}");
                                }

                                if (take >= thing.stackCount)
                                {
                                    _virtualThings.Remove(thing.thingIDNumber);
                                }
                            }
                        }
                        remaining -= take;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[DigitalStorage][PH] DeleteSelectedPrefix error: {ex.Message}");
            }
        }

        public static void GetSelectedAsProtoPostfix(object __instance)
        {
            try
            {
                Type type = __instance.GetType();
                FieldInfo thingsField = type.GetField("Things", BindingFlags.Public | BindingFlags.Instance);
                FieldInfo selectedField = type.GetField("Selected", BindingFlags.Public | BindingFlags.Instance);

                if (thingsField == null || selectedField == null) return;

                List<Thing> things = thingsField.GetValue(__instance) as List<Thing>;
                int selected = (int)selectedField.GetValue(__instance);

                if (things == null || selected <= 0) return;

                bool hasVirtual = things.Any(t => t != null && IsVirtualThing(t));
                if (!hasVirtual) return;

                int remaining = selected;
                foreach (Thing thing in things)
                {
                    if (remaining <= 0) break;
                    if (thing == null) continue;

                    int toTake = System.Math.Min(thing.stackCount, remaining);

                    if (IsVirtualThing(thing) && toTake > 0)
                    {
                        VirtualThingInfo info;
                        lock (_lock)
                        {
                            if (_virtualThings.TryGetValue(thing.thingIDNumber, out info))
                            {
                                if (info.sourceCore != null && info.sourceCore.Spawned && info.sourceCore.Powered)
                                {
                                    int deducted = info.sourceCore.DeductVirtualItems(info.def, toTake);
                                    Log.Message($"[DigitalStorage][PH] GetSelectedAsProto: deducted {info.def?.label} x{toTake}, actual={deducted}");
                                }
                            }
                        }
                    }
                    remaining -= toTake;
                }
            }
            catch (Exception ex)
            {
                Log.Warning($"[DigitalStorage][PH] GetSelectedAsProtoPostfix error: {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(Thing), "DeSpawn")]
    public static class Patch_Thing_DeSpawn_PhinixCompat
    {
        public static bool Prefix(Thing __instance)
        {
            if (PhinixCompatibility.IsVirtualThing(__instance) && !__instance.Spawned)
            {
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(Thing), "SplitOff")]
    public static class Patch_Thing_SplitOff_PhinixCompat
    {
        public static void Postfix(Thing __instance, Thing __result)
        {
            if (__result == null || __result == __instance) return;

            if (PhinixCompatibility.IsVirtualThing(__instance))
            {
                PhinixCompatibility.VirtualThingInfo info;
                lock (PhinixCompatibility._lock)
                {
                    if (PhinixCompatibility._virtualThings.TryGetValue(__instance.thingIDNumber, out info))
                    {
                        PhinixCompatibility._virtualThings[__result.thingIDNumber] = new PhinixCompatibility.VirtualThingInfo
                        {
                            sourceCore = info.sourceCore,
                            def = info.def
                        };
                    }
                }
            }
        }
    }
}
