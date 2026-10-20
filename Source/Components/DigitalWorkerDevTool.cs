using System.Collections.Generic;
using System.Text;
using DigitalStorage.AI;
using LudeonTK;
using RimWorld;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 代理建筑的**开发者排查工具**：点一台代理建筑 → 打印"它到底在扫哪些活、每条 giver 为什么被收 / 被拒"。
    ///
    /// <para><b>为什么需要它</b>（起因：2026-10 玩家上报「<b>种植代理可以建造</b>」，本机复现不出来）：
    /// 代理建筑的活是两道门算出来的 ——
    /// ① <c>WorkGiverDef.workType</c> 必须落在建筑的 <c>workTypes</c> 里（<c>Defs/ThingDefs_Workers.xml</c>）；
    /// ② 那个 giver 的类必须被某个 <see cref="DigitalTaskAdapter"/> 认领，而且
    ///    <see cref="DigitalTaskAdapter.HomeWorkTypes"/> 得允许这个工作类型。
    /// <b>两道门都是静默的</b>：被判掉的 giver 连一条日志都没有 ——
    /// 玩家看到的只有"建筑干了不该干的活"或"该干的活不干"。这条工具把整张表打出来，
    /// 而且**每一条判定都真的调了产品代码里的那个方法**（<c>FindGivers</c> / <c>AdapterFor</c> /
    /// <c>AcceptsWorkType</c>），不另写近似逻辑（本 mod 在诊断上吃过这个亏）。</para>
    ///
    /// <para>第三段"全库扫描"是给"某个 mod 把 giver 挪进了别的工作类型"准备的：
    /// 它列出**所有**类属于我们适配器、但工作类型不在本建筑列表里的 giver
    /// —— 想知道"谁把建造族的 giver 塞进了 <c>Growing</c>"，就看这一段。</para>
    /// </summary>
    internal static class DigitalWorkerDevTool
    {
        private const string Category = "DigitalStorage";

        /// <summary>全库扫描最多打印几条（防一次把日志刷爆）。</summary>
        private const int MaxForeignLines = 40;

        private static readonly StringBuilder SB = new StringBuilder();

        [DebugAction(Category, "这个代理建筑在扫哪些活（点建筑）",
            actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DiagnoseWorker()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;

            // ⚠️ 必须写全 `Verse.UI` —— 本 mod 自己有个 DigitalStorage.UI 命名空间，
            // 裸写 `UI.MouseCell()` 会被解析成 DigitalStorage.UI（编译期就报 CS0234）。
            IntVec3 cell = Verse.UI.MouseCell();
            if (!cell.InBounds(map)) return;

            CompDigitalWorker comp = WorkerAt(map, cell);
            if (comp == null)
            {
                Log.Warning("[DS 代理] 这一格（" + cell.x + ", " + cell.z + "）上没有代理建筑"
                            + "（要含 CompDigitalWorker 的建筑）。");
                return;
            }

            MoteMaker.ThrowText(comp.parent.DrawPos, map, "DS 代理诊断 → 日志");

            CompProperties_DigitalWorker props = comp.Props;
            List<WorkTypeDef> types = (props == null) ? null : props.workTypes;
            List<WorkTypeDef> effective = comp.EffectiveWorkTypes;

            SB.Length = 0;
            SB.AppendLine("=== [DS 代理] " + comp.parent.LabelCap.ToString() + "  def=" + comp.parent.def.defName + " ===");
            SB.AppendLine("  启用的工作类型（XML 配置）：" + WorkTypeNames(types));
            SB.AppendLine("  实际扫描的工作类型：" + WorkTypeNames(effective)
                          + "   ← 多出来的是「跟随」（canonical giver 被别的 mod 搬去了别的工作类型；2026-10-20 修）");
            SB.AppendLine("  在干的活：" + comp.ActiveCount + " 件 / 第一件 = "
                          + (comp.CurrentTask == null ? "(无)" : comp.CurrentTask.Label + " → " + comp.CurrentTask.TargetLabel));
            SB.AppendLine("  状态：启用=" + comp.Enabled + " 通电=" + comp.Powered + " 能干活=" + comp.CanWork
                          + " 扫描间隔=" + (props == null ? 0 : props.scanIntervalTicks) + " tick");

            // 刻意用 WorkerIfCreated：诊断不该有副作用（点一下不该顺手造一个假 pawn）
            Pawn worker = comp.WorkerIfCreated;
            SB.AppendLine("  资质载体（假 pawn）：" + (worker == null
                ? "(还没建；建筑还没干过活)"
                : worker.LabelShort + "  固定资质=" + (props == null ? 0 : props.skillLevel)));

            AppendPerWorkType(effective);
            AppendForeignGivers(effective);

            SB.AppendLine("=== [DS 代理] 结束 ===");
            Log.Warning(SB.ToString());
        }

        /// <summary>逐工作类型：先列"真闸门放行的 giver"，再逐条 WorkGiverDef 给出判定与来路。</summary>
        private static void AppendPerWorkType(List<WorkTypeDef> types)
        {
            if (types == null || types.Count == 0)
            {
                SB.AppendLine("  ⚠️ 这台建筑一个工作类型都没配（XML 的 workTypes 为空 / defName 写错）⇒ 它什么都不会扫。");
                return;
            }

            List<WorkGiverDef> all = DefDatabase<WorkGiverDef>.AllDefsListForReading;

            for (int i = 0; i < types.Count; i++)
            {
                WorkTypeDef wt = types[i];
                if (wt == null)
                {
                    SB.AppendLine("  · (null) —— workTypes 里有个 defName 解析不出来（写错字 / 那个工作类型所属的 mod 没装）");
                    continue;
                }

                // ① 真闸门：产品代码实际会用的那个列表（含两道门）
                List<WorkGiver> givers = DigitalTaskRegistry.FindGivers(wt);
                SB.AppendLine("  · 工作类型 " + wt.defName + "（" + wt.labelShort + "）"
                              + " ⇒ 实际会用的 giver：" + GiverNames(givers));

                // ② 逐条 WorkGiverDef：判定 + 原因（同一个 API，不另写近似）
                for (int d = 0; d < all.Count; d++)
                {
                    WorkGiverDef def = all[d];
                    if (def == null || def.workType != wt) continue;

                    DigitalTaskAdapter adapter = DigitalTaskRegistry.AdapterFor(def.Worker);
                    string verdict;
                    if (adapter == null)
                    {
                        verdict = "✗ 没有适配器（这类活我们不认识）";
                    }
                    else if (!adapter.AcceptsWorkType(wt))
                    {
                        verdict = "✗ 被 HomeWorkTypes 拒绝：" + adapter.GetType().Name
                                  + " 只认 " + JoinStrings(adapter.HomeWorkTypes)
                                  + " + 跟随集 " + WorkTypeNames(adapter.FollowedWorkTypes);
                    }
                    else if (!adapter.AcceptsByHomeName(wt))
                    {
                        verdict = "✓ " + adapter.GetType().Name + "（跟随：canonical giver 现在挂在这个工作类型下）";
                    }
                    else
                    {
                        verdict = "✓ " + adapter.GetType().Name;
                    }

                    SB.AppendLine("        - " + def.defName + "  [" + ClassName(def) + "]  " + verdict);
                }
            }
        }

        /// <summary>
        /// <b>全库扫描</b>：类属于我们的适配器、但工作类型不在本建筑列表里的 giver。
        /// 这一段的用途就一个 —— 揪出"把某族 giver 挪进别的工作类型"的 mod
        /// （例：谁把 <c>WorkGiver_ConstructFinishFrames</c> 那一族放进了 <c>Growing</c>）。
        /// </summary>
        private static void AppendForeignGivers(List<WorkTypeDef> types)
        {
            List<WorkGiverDef> all = DefDatabase<WorkGiverDef>.AllDefsListForReading;
            SB.AppendLine("  · 全库扫描（类属于我们、但工作类型不在本建筑列表里）：");

            int total = 0;
            int shown = 0;
            for (int i = 0; i < all.Count; i++)
            {
                WorkGiverDef def = all[i];
                if (def == null) continue;

                DigitalTaskAdapter adapter = DigitalTaskRegistry.AdapterFor(def.Worker);
                if (adapter == null) continue;
                if (ContainsWorkType(types, def.workType)) continue;

                total++;
                if (shown >= MaxForeignLines) continue;
                shown++;
                SB.AppendLine("        - " + DefNameOf(def.workType) + " / " + def.defName
                              + "  [" + ClassName(def) + "]  → " + adapter.GetType().Name
                              + "（本建筑没配这个工作类型，所以不会干）");
            }

            if (total == 0) SB.AppendLine("        (无 —— 全库里没有类属于我们但落在本建筑工作类型之外的 giver)");
            else if (total > shown) SB.AppendLine("        ……共 " + total + " 条，只列了前 " + shown + " 条");
        }

        // ===================================================================
        // 小工具
        // ===================================================================

        private static CompDigitalWorker WorkerAt(Map map, IntVec3 cell)
        {
            List<Thing> things = map.thingGrid.ThingsListAt(cell);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t == null || t.Destroyed) continue;
                CompDigitalWorker c = t.TryGetComp<CompDigitalWorker>();
                if (c != null) return c;
            }
            return null;
        }

        private static bool ContainsWorkType(List<WorkTypeDef> types, WorkTypeDef wt)
        {
            if (types == null || wt == null) return false;
            for (int i = 0; i < types.Count; i++)
            {
                if (ReferenceEquals(types[i], wt)) return true;
            }
            return false;
        }

        private static string WorkTypeNames(List<WorkTypeDef> types)
        {
            if (types == null || types.Count == 0) return "(空)";
            string s = null;
            for (int i = 0; i < types.Count; i++)
            {
                string n = (types[i] == null) ? "(null)" : types[i].defName;
                s = (s == null) ? n : (s + "+" + n);
            }
            return s;
        }

        private static string GiverNames(List<WorkGiver> givers)
        {
            if (givers == null || givers.Count == 0) return "(无)";
            string s = null;
            for (int i = 0; i < givers.Count; i++)
            {
                WorkGiver g = givers[i];
                string n = (g == null || g.def == null) ? "(null)" : g.def.defName;
                s = (s == null) ? n : (s + ", " + n);
            }
            return s;
        }

        private static string JoinStrings(string[] arr)
        {
            if (arr == null || arr.Length == 0) return "(不限制)";
            string s = null;
            for (int i = 0; i < arr.Length; i++)
            {
                s = (s == null) ? arr[i] : (s + "/" + arr[i]);
            }
            return s;
        }

        private static string ClassName(WorkGiverDef def)
        {
            return (def == null || def.giverClass == null) ? "?" : def.giverClass.Name;
        }

        private static string DefNameOf(WorkTypeDef wt)
        {
            return (wt == null) ? "(无工作类型)" : wt.defName;
        }
    }
}
