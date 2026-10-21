using System.Collections.Generic;
using System.Text;
using DigitalStorage.AI;
using LudeonTK;
using RimWorld;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 制作代理的**开发者排查工具**：点一下，把"这台工作台到底为什么没被代理用上"整条链路的判定打出来。
    ///
    /// <para><b>起因</b>：社区反馈第 2 条 ——「制作代理的整间工作间只认出切石砖 / 缝杂皮 / EMP 炮弹，
    /// 其他配方都不显示」。这条反馈背后至少有四种**互相独立**的死法，而且全都**不写日志**：</para>
    /// <list type="number">
    /// <item><b>距离</b>：台子在扫描方形之外（见 <see cref="CompBillAutomation.EffectiveScanRadius"/>）；
    ///   老版本还只看台子左下角一格 —— 3×1 的台子压在线外半格就整台消失。</item>
    /// <item><b>白名单</b>：原版 <c>WorkGiver_DoBill.ThingIsUsableBillGiver</c> 只认
    ///   <c>fixedBillGiverDefs</c> 里点名的台子（19 个 DoBill giver 里 16 个是精确 defName），
    ///   mod 台子一个都不在名单里 ⇒ 面板能选、代理永远取不到活。
    ///   本 mod 用 <see cref="BenchWhitelistScope"/> 在"没有 giver 认领"时放宽这一条。</item>
    /// <item><b>配方层</b>：<c>RecipeDef.AvailableNow</c>（研究 / meme / 派系标签）会挡在
    ///   <see cref="CraftUnlocks.MenuRecipes"/> 里。</item>
    /// <item><b>认领</b>：一台台子同时只归一个代理（<see cref="BillBenchClaims"/>），
    ///   两台代理范围重叠时后来者永远扫不到。</item>
    /// </list>
    ///
    /// <para>这个工具**直接调产品代码**（<see cref="CompBillAutomation.InScanRange"/>、
    /// <see cref="CraftUnlocks.For"/>、<see cref="BillBenchClaims.OwnerOf"/>），不另写近似判定 ——
    /// 诊断逻辑与产品逻辑一旦分叉，工具就会开始撒谎（本 mod 在诊断上吃过这个亏）。</para>
    /// </summary>
    internal static class CraftAutomationDevTool
    {
        private const string Category = "DigitalStorage";

        private static readonly StringBuilder SB = new StringBuilder();
        private static readonly List<CompBillAutomation> tmpProxies = new List<CompBillAutomation>();
        private static readonly List<WorkGiverDef> tmpGivers = new List<WorkGiverDef>();

        // ===================================================================
        // ① 点工作台：为什么没被识别
        // ===================================================================

        [DebugAction(Category, "制作代理：这台工作台为什么没被识别（点工作台）",
            actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DiagnoseBench()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;

            // ⚠️ 必须写全 `Verse.UI`（本 mod 有 DigitalStorage.UI 命名空间，裸写会 CS0234）
            IntVec3 cell = Verse.UI.MouseCell();
            if (!cell.InBounds(map)) return;

            Thing bench = BenchAt(map, cell);
            if (bench == null)
            {
                Log.Warning("[DS 制作] 这一格（" + cell.x + ", " + cell.z + "）上没有可做工的建筑"
                            + "（要 Building + IBillGiver）。多格台子请点它占的任一格。");
                return;
            }

            MoteMaker.ThrowText(bench.DrawPos, map, "DS 台子诊断 → 日志");

            SB.Length = 0;
            SB.AppendLine("=== [DS 制作] 工作台 " + bench.LabelCap + "  def=" + bench.def.defName
                          + "  位置=" + bench.PositionHeld + " ===");
            SB.AppendLine("  占位矩形=" + bench.OccupiedRect() + "  有交互格=" + bench.def.hasInteractionCell
                          + (bench.def.hasInteractionCell ? ("  交互格=" + bench.InteractionCell) : ""));
            AppendRecipeStats(bench);
            AppendGiverCoverage(bench);
            AppendProxyCoverage(map, bench);

            SB.AppendLine("=== [DS 制作] 结束 ===");
            Log.Warning(SB.ToString());
        }

        // ===================================================================
        // ② 点代理：范围内台子与配方计数
        // ===================================================================

        [DebugAction(Category, "制作代理：范围内台子与配方计数（点代理）",
            actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DiagnoseProxy()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;

            IntVec3 cell = Verse.UI.MouseCell();
            if (!cell.InBounds(map)) return;

            CompBillAutomation comp = ProxyAt(map, cell);
            if (comp == null)
            {
                Log.Warning("[DS 制作] 这一格（" + cell.x + ", " + cell.z + "）上没有制作代理。");
                return;
            }

            MoteMaker.ThrowText(comp.parent.DrawPos, map, "DS 代理诊断 → 日志");

            int radius = comp.EffectiveScanRadius;
            SB.Length = 0;
            SB.AppendLine("=== [DS 制作] " + comp.parent.LabelCap + "  def=" + comp.parent.def.defName + " ===");
            SB.AppendLine("  设置：扫描半径=" + radius + " 格（" + (radius * 2 + 1) + "×" + (radius * 2 + 1)
                          + "）  识别 mod 台子=" + DigitalStorage.Settings.DigitalStorageSettings.craftAnyBench
                          + "  XML scanRadius=" + comp.Props.scanRadius + "（已不参与计算，仅兼容）");
            SB.AppendLine("  本次扫描结果：台子 " + comp.BenchesForReading.Count + " 台 / 可选配方 "
                          + comp.UnlockedRecipes.Count + " 个");

            if (comp.BenchesForReading.Count == 0)
            {
                SB.AppendLine("  ⚠️ 一台都没扫到：先确认台子是 Building + IBillGiver 且在范围内（看上面半径）。");
            }
            else
            {
                for (int i = 0; i < comp.BenchesForReading.Count; i++)
                {
                    Thing b = comp.BenchesForReading[i];
                    if (b == null || b.Destroyed) continue;
                    CompBillAutomation owner = BillBenchClaims.OwnerOf(map, b);
                    SB.AppendLine("    · " + b.def.defName + " @" + b.PositionHeld
                                  + "  配方=" + SafeRecipeCount(b)
                                  + "  认领=" + (owner == null ? "无人" : (owner == comp ? "本代理" : "(其它代理)")));
                }
            }

            AppendProxyLineage(map, comp);
            SB.AppendLine("=== [DS 制作] 结束 ===");
            Log.Warning(SB.ToString());
        }

        // ===================================================================
        // 打印段
        // ===================================================================

        private static void AppendRecipeStats(Thing bench)
        {
            List<RecipeDef> all = bench.def.AllRecipes;
            List<RecipeDef> auto = CraftUnlocks.For(bench.def);
            int available = 0;
            for (int i = 0; i < auto.Count; i++)
            {
                if (auto[i] != null && auto[i].AvailableNow) available++;
            }

            SB.AppendLine("  配方：AllRecipes=" + (all == null ? 0 : all.Count)
                          + "  自动化候选=" + auto.Count
                          + "  研究后可用=" + available);
            if (all == null || all.Count == 0)
            {
                SB.AppendLine("  ⚠️ AllRecipes 为空 ⇒ 这个 def 根本不在 ThingRequestGroup.PotentialBillGiver 里，"
                              + "原版与代理都不会把它当工作台（多半是 XML 少了 recipeUsers / recipes）。");
            }
            else if (available == 0)
            {
                SB.AppendLine("  ⚠️ 候选配方全被 AvailableNow 挡着（研究 / meme / 派系标签）⇒ 面板里不会出现。");
                AppendMissingResearch(auto);
            }
        }

        /// <summary>把"挡着这些配方的研究"列出来（Mines 2.0 的矿场就是这样：配方全挂在 minecraft 下）。</summary>
        private static void AppendMissingResearch(List<RecipeDef> recipes)
        {
            if (recipes == null || recipes.Count == 0) return;
            List<string> names = new List<string>();
            for (int i = 0; i < recipes.Count; i++)
            {
                ResearchProjectDef need = CraftUnlocks.ResearchLock(recipes[i]);
                if (need == null) continue;
                string label = need.LabelCap.ToString();
                if (!names.Contains(label)) names.Add(label);
                if (names.Count >= 3) break;
            }
            if (names.Count == 0) return;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < names.Count; i++)
            {
                if (i > 0) sb.Append(" / ");
                sb.Append(names[i]);
            }
            SB.AppendLine("    缺的研究：" + sb);
        }

        /// <summary>
        /// 原版 DoBill giver 对这型台子认不认：认领的 defName 列出来，没认领的给个数。
        /// 没有 giver 认领 ⇒ 代理靠 <see cref="BenchWhitelistScope"/> 放宽才有活；
        /// 这里同时告诉玩家"放宽开关有没有开"。
        /// </summary>
        private static void AppendGiverCoverage(Thing bench)
        {
            tmpGivers.Clear();
            List<WorkGiverDef> all = DefDatabase<WorkGiverDef>.AllDefsListForReading;
            int doBillTotal = 0;
            for (int i = 0; i < all.Count; i++)
            {
                WorkGiverDef d = all[i];
                if (d == null || d.giverClass == null) continue;
                if (!typeof(WorkGiver_DoBill).IsAssignableFrom(d.giverClass)) continue;
                doBillTotal++;
                if (d.fixedBillGiverDefs != null && d.fixedBillGiverDefs.Contains(bench.def)) tmpGivers.Add(d);
            }

            SB.AppendLine("  原版 DoBill giver：" + doBillTotal + " 个，其中认领这型台子的 "
                          + tmpGivers.Count + " 个");
            if (tmpGivers.Count > 0)
            {
                StringBuilder names = new StringBuilder();
                for (int i = 0; i < tmpGivers.Count; i++)
                {
                    if (i > 0) names.Append(", ");
                    names.Append(tmpGivers[i].defName);
                }
                SB.AppendLine("    认领者：" + names);
            }
            else
            {
                SB.AppendLine("    ⚠️ 没有 giver 认领 ⇒ 原版口径下代理**永远取不到活**；"
                              + "放宽开关（识别 mod 工作台）="
                              + DigitalStorage.Settings.DigitalStorageSettings.craftAnyBench
                              + (DigitalStorage.Settings.DigitalStorageSettings.craftAnyBench
                                  ? "，已开 ⇒ 代理会放宽这道白名单"
                                  : "，**已关** ⇒ 把设置里这一项打开"));
            }
            tmpGivers.Clear();
        }

        /// <summary>每台代理对这张台子的覆盖判定：在不在范围、认领归谁、那个代理正在做什么。</summary>
        private static void AppendProxyCoverage(Map map, Thing bench)
        {
            CollectProxies(map, tmpProxies);
            SB.AppendLine("  本图制作代理：" + tmpProxies.Count + " 台");
            if (tmpProxies.Count == 0)
            {
                SB.AppendLine("    ⚠️ 本图没有制作代理。");
            }

            for (int i = 0; i < tmpProxies.Count; i++)
            {
                CompBillAutomation comp = tmpProxies[i];
                if (comp == null || comp.parent == null) continue;

                int radius = comp.EffectiveScanRadius;
                CellRect rect = CellRect.CenteredOn(comp.parent.PositionHeld, radius);
                bool inRange = CompBillAutomation.InScanRange(bench, rect);
                int dist = (int)bench.PositionHeld.DistanceTo(comp.parent.PositionHeld);
                CompBillAutomation owner = BillBenchClaims.OwnerOf(map, bench);

                SB.AppendLine("    · 代理 @" + comp.parent.PositionHeld + " 半径=" + radius
                              + "  距离=" + dist + "  在范围内=" + inRange
                              + "  认领=" + (owner == null ? "无人" : (owner == comp ? "本代理" : "(另一台代理)")));
                if (!inRange)
                {
                    SB.AppendLine("        ↳ 台子占位矩形 " + bench.OccupiedRect() + " 与扫描矩形 "
                                  + rect + " 不相交，交互格也不在内 ⇒ 调大扫描半径或把代理挪近。");
                }
            }

            tmpProxies.Clear();
        }

        /// <summary>代理自己的范围里"漏掉了哪些台子"——反向看：地图上所有 DoBill 台子与范围的距离。</summary>
        private static void AppendProxyLineage(Map map, CompBillAutomation comp)
        {
            int radius = comp.EffectiveScanRadius;
            CellRect rect = CellRect.CenteredOn(comp.parent.PositionHeld, radius);
            List<Thing> all = map.listerThings.ThingsInGroup(ThingRequestGroup.PotentialBillGiver);
            int total = 0;
            int missed = 0;
            for (int i = 0; i < all.Count; i++)
            {
                Thing t = all[i];
                if (t == null || t.Destroyed || !(t is Building) || !(t is IBillGiver) || t == comp.parent) continue;
                total++;
                if (CompBillAutomation.InScanRange(t, rect)) continue;
                missed++;
                if (missed <= 12)
                {
                    SB.AppendLine("    漏： " + t.def.defName + " @" + t.PositionHeld
                                  + "  距离=" + (int)t.PositionHeld.DistanceTo(comp.parent.PositionHeld)
                                  + "  占位=" + t.OccupiedRect());
                }
            }
            SB.AppendLine("  地图上可做工建筑 " + total + " 台，范围外 " + missed + " 台"
                          + (missed > 12 ? "（只列前 12 台）" : ""));
        }

        // ===================================================================
        // 小工具
        // ===================================================================

        private static int SafeRecipeCount(Thing bench)
        {
            List<RecipeDef> rs = CraftUnlocks.For(bench.def);
            return rs == null ? 0 : rs.Count;
        }

        private static Thing BenchAt(Map map, IntVec3 cell)
        {
            List<Thing> things = map.thingGrid.ThingsListAt(cell);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t == null || t.Destroyed) continue;
                if (t is Building && t is IBillGiver) return t;
            }
            return null;
        }

        private static CompBillAutomation ProxyAt(Map map, IntVec3 cell)
        {
            List<Thing> things = map.thingGrid.ThingsListAt(cell);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t == null || t.Destroyed) continue;
                CompBillAutomation c = t.TryGetComp<CompBillAutomation>();
                if (c != null) return c;
            }
            return null;
        }

        private static void CollectProxies(Map map, List<CompBillAutomation> outList)
        {
            outList.Clear();
            List<Thing> all = map.listerThings.AllThings;
            for (int i = 0; i < all.Count; i++)
            {
                Thing t = all[i];
                if (t == null || t.Destroyed) continue;
                CompBillAutomation c = t.TryGetComp<CompBillAutomation>();
                if (c != null) outList.Add(c);
            }
        }
    }
}
