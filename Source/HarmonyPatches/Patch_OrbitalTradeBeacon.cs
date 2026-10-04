using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using DigitalStorage.Components;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    // 只追加可用核心的直接内容物, 避免改变原版容器规则
    [HarmonyPatch(typeof(TradeUtility), "AllLaunchableThingsForTrade")]
    static class Patch_AllLaunchableThingsForTrade
    {
        static IEnumerable<Thing> Postfix(IEnumerable<Thing> __result, Map map, ITrader trader)
        {
            var yielded = new HashSet<Thing>(ThingIdentityComparer.Instance);
            if (__result != null)
            {
                foreach (Thing thing in __result)
                {
                    if (yielded.Add(thing)) yield return thing;
                }
            }

            foreach (Thing thing in DigitalStorageTradeUtility.CoreContents(map, trader))
            {
                if (yielded.Add(thing)) yield return thing;
            }
        }
    }

    // 只补地面扫描失败后的候选, 让原版负责数量和交易转移
    [HarmonyPatch(typeof(TradeUtility), "LaunchThingsOfType", new[] { typeof(ThingDef), typeof(int), typeof(Map), typeof(TradeShip) })]
    internal static class Patch_LaunchThingsOfType
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            var code = new List<CodeInstruction>(instructions);
            MethodInfo min = AccessTools.Method(typeof(Math), nameof(Math.Min), new[] { typeof(int), typeof(int) });
            MethodInfo error = AccessTools.Method(typeof(Log), nameof(Log.Error), new[] { typeof(string) });
            MethodInfo paymentThing = AccessTools.Method(typeof(DigitalStorageTradeUtility), nameof(DigitalStorageTradeUtility.PaymentThing));
            FieldInfo stackCount = AccessTools.Field(typeof(Thing), nameof(Thing.stackCount));
            MethodBody body = __originalMethod.GetMethodBody();
            if (min == null || error == null || paymentThing == null || stackCount == null || body == null)
                throw new InvalidOperationException("[DigitalStorage] LaunchThingsOfType payment hook metadata is unavailable.");

            int countStart = -1;
            int thingLocal = -1;
            for (int i = 3; i < code.Count; i++)
            {
                if (!code[i].Calls(min) || code[i - 1].opcode != OpCodes.Ldfld || !stackCount.Equals(code[i - 1].operand)
                    || !LoadsArgument(code[i - 3], 1)) continue;
                int local = LocalIndex(code[i - 2], false);
                if (local < 0 || local >= body.LocalVariables.Count || body.LocalVariables[local].LocalType != typeof(Thing)) continue;
                if (countStart >= 0)
                    throw new InvalidOperationException("[DigitalStorage] LaunchThingsOfType has multiple payment count sites.");
                countStart = i - 3;
                thingLocal = local;
            }
            if (countStart < 0)
                throw new InvalidOperationException("[DigitalStorage] LaunchThingsOfType payment count site was not found.");

            CodeInstruction store = null;
            for (int i = 1; i < countStart; i++)
            {
                if (code[i - 1].opcode != OpCodes.Ldnull || LocalIndex(code[i], true) != thingLocal) continue;
                if (store != null)
                    throw new InvalidOperationException("[DigitalStorage] LaunchThingsOfType has multiple payment candidate initializers.");
                store = code[i];
            }

            int check = -1;
            for (int i = 0; i + 2 < countStart; i++)
            {
                if (LocalIndex(code[i], false) != thingLocal
                    || (code[i + 1].opcode != OpCodes.Brtrue && code[i + 1].opcode != OpCodes.Brtrue_S)
                    || !(code[i + 1].operand is Label target) || !code[countStart].labels.Contains(target)
                    || code[i + 2].opcode != OpCodes.Ldstr || !Equals(code[i + 2].operand, "Could not find any ")) continue;

                bool logsError = false;
                bool returns = false;
                for (int j = i + 3; j < countStart; j++)
                {
                    if (code[j].Calls(error)) logsError = true;
                    if (logsError && code[j].opcode == OpCodes.Ret) returns = true;
                }
                if (!returns) continue;
                if (check >= 0)
                    throw new InvalidOperationException("[DigitalStorage] LaunchThingsOfType has multiple missing-payment checks.");
                check = i;
            }
            if (store == null || check < 0)
                throw new InvalidOperationException("[DigitalStorage] LaunchThingsOfType missing-payment branch did not match the supported vanilla shape.");

            var first = new CodeInstruction(code[check].opcode, code[check].operand);
            first.labels.AddRange(code[check].labels);
            first.blocks.AddRange(code[check].blocks);
            code[check].labels.Clear();
            code[check].blocks.Clear();
            code.InsertRange(check, new[]
            {
                first,
                CodeInstruction.LoadArgument(0),
                CodeInstruction.LoadArgument(2),
                CodeInstruction.LoadArgument(3),
                new CodeInstruction(OpCodes.Call, paymentThing),
                new CodeInstruction(store.opcode, store.operand)
            });
            return code;
        }

        private static bool LoadsArgument(CodeInstruction instruction, int index)
        {
            if (index == 1 && instruction.opcode == OpCodes.Ldarg_1) return true;
            return (instruction.opcode == OpCodes.Ldarg || instruction.opcode == OpCodes.Ldarg_S)
                && Convert.ToInt32(instruction.operand) == index;
        }

        private static int LocalIndex(CodeInstruction instruction, bool store)
        {
            OpCode opcode = instruction.opcode;
            if (opcode == (store ? OpCodes.Stloc_0 : OpCodes.Ldloc_0)) return 0;
            if (opcode == (store ? OpCodes.Stloc_1 : OpCodes.Ldloc_1)) return 1;
            if (opcode == (store ? OpCodes.Stloc_2 : OpCodes.Ldloc_2)) return 2;
            if (opcode == (store ? OpCodes.Stloc_3 : OpCodes.Ldloc_3)) return 3;
            if (opcode != (store ? OpCodes.Stloc : OpCodes.Ldloc) && opcode != (store ? OpCodes.Stloc_S : OpCodes.Ldloc_S)) return -1;
            if (instruction.operand is LocalBuilder builder) return builder.LocalIndex;
            if (instruction.operand is LocalVariableInfo local) return local.LocalIndex;
            return Convert.ToInt32(instruction.operand);
        }
    }

    internal static class DigitalStorageTradeUtility
    {
        internal static IEnumerable<Thing> CoreContents(Map map, ITrader trader)
        {
            List<IHaulSource> sources = map?.haulDestinationManager?.AllHaulSourcesListForReading;
            if (sources == null) yield break;

            for (int i = 0; i < sources.Count; i++)
            {
                Building_StorageCore core = sources[i] as Building_StorageCore;
                if (core == null || !core.IsUsableNow || !core.HaulSourceEnabled) continue;

                ThingOwner held = core.GetDirectlyHeldThings();
                if (held == null) continue;
                for (int j = 0; j < held.Count; j++)
                {
                    Thing thing = held[j];
                    if (!SellableContent(thing, trader)) continue;
                    yield return thing;
                }
            }
        }

        internal static Thing PaymentThing(Thing groundThing, ThingDef resDef, Map map, TradeShip trader)
        {
            if (groundThing != null) return groundThing;
            List<IHaulSource> sources = map?.haulDestinationManager?.AllHaulSourcesListForReading;
            if (sources == null) return null;
            for (int i = 0; i < sources.Count; i++)
            {
                Building_StorageCore core = sources[i] as Building_StorageCore;
                if (core == null || !core.IsUsableNow || !core.HaulSourceEnabled) continue;
                ThingOwner held = core.GetDirectlyHeldThings();
                if (held == null) continue;
                for (int j = 0; j < held.Count; j++)
                {
                    Thing thing = held[j];
                    if (thing != null && thing.def == resDef && SellableContent(thing, trader)) return thing;
                }
            }
            return null;
        }

        private static bool SellableContent(Thing thing, ITrader trader)
        {
            return thing != null && !thing.Destroyed && thing.def != null && thing.stackCount > 0
                && !(thing is Pawn) && TradeUtility.PlayerSellableNow(thing, trader);
        }
    }

    internal sealed class ThingIdentityComparer : IEqualityComparer<Thing>
    {
        internal static readonly ThingIdentityComparer Instance = new ThingIdentityComparer();

        public bool Equals(Thing left, Thing right) => ReferenceEquals(left, right);

        public int GetHashCode(Thing thing) => thing == null ? 0 : RuntimeHelpers.GetHashCode(thing);
    }
}
