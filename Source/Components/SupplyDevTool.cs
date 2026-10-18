using System.Collections.Generic;
using System.Text;
using DigitalStorage.AI;
using DigitalStorage.Core;
using LudeonTK;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 右键蓝图 / 框「为什么没有取料选项」的逐道门诊断（2026-10-18 用户反馈：
    /// 地上没材料时右键蓝图没有任何反应 —— 原版 <c>FloatMenuOptionProvider_WorkGivers</c>
    /// 在 <c>HasJobOnThing</c> 失败且没有 <c>JobFailReason</c> 时**连菜单项都不给**，
    /// 所以只能把每一道门单独打印出来才知道卡在哪）。
    ///
    /// <para>本工具打印的每一项都调**产品代码里的同一个方法**
    /// （<see cref="DSConstructionDelivery.TryMakeJob"/>、<see cref="CoreFinder.AnyUsableCoreGlobal"/>、
    /// <see cref="HaulSourceContents.CountOf"/>），不另写近似判据。</para>
    /// </summary>
    internal static class SupplyDevTool
    {
        private const string Category = "DigitalStorage";

        [DebugAction(Category, "建造取料：这件蓝图为什么没有取料选项（点蓝图/框）",
            actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DiagnoseSupplyGate()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;
            IntVec3 cell = Verse.UI.MouseCell();
            if (!cell.InBounds(map)) return;

            Thing target = null;
            List<Thing> things = cell.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Blueprint || things[i] is Frame)
                {
                    target = things[i];
                    break;
                }
            }

            var sb = new StringBuilder();
            if (target == null)
            {
                Log.Warning("[DS 建造取料] 这一格（" + cell.x + ", " + cell.z + "）没有蓝图 / 框。");
                return;
            }

            sb.AppendLine("[DS 建造取料] " + target.LabelShort + " (" + target.def.defName + ") @ ("
                          + cell.x + ", " + cell.z + ")");

            Pawn pawn = Find.Selector.SingleSelectedThing as Pawn;
            if (pawn == null)
                sb.AppendLine("· 没有选中单个小人 —— 右键菜单只对选中的小人出现（多选时原版 work-giver 菜单整体关闭）。");

            IConstructible c = target as IConstructible;
            sb.AppendLine("· IConstructible: " + (c != null ? "是" : "否 ⇒ 原版与我们都当不了建造目标"));
            if (c == null)
            {
                Log.Warning(sb.ToString());
                return;
            }

            // ① 账单：还差多少 / 核心里有多少
            List<ThingDefCountClass> cost = null;
            try
            {
                cost = c.TotalMaterialCost();
            }
            catch (System.Exception ex)
            {
                sb.AppendLine("· TotalMaterialCost 抛异常: " + ex.Message);
            }

            if (cost != null)
            {
                sb.AppendLine("· 账单 " + cost.Count + " 项：");
                for (int i = 0; i < cost.Count; i++)
                {
                    ThingDefCountClass m = cost[i];
                    if (m?.thingDef == null) continue;
                    int need;
                    try { need = c.ThingCountNeeded(m.thingDef); }
                    catch { need = -999; }
                    sb.AppendLine("    - " + m.thingDef.defName + " · 账单 " + m.count + " · 还差 " + need
                                  + " · 本图核心里有 " + HaulSourceContents.CountOf(map, m.thingDef));
                }
            }

            if (pawn == null)
            {
                Log.Warning(sb.ToString());
                return;
            }

            // ② 逐道门（顺序与原版 / 我们的 WorkGiver 一致）
            sb.AppendLine("· 小人 " + pawn.LabelShort + "：");
            sb.AppendLine("    - Spawned / 同图: " + pawn.Spawned + " / " + (pawn.Map == map));
            sb.AppendLine("    - 可用核心（含其它图）: " + CoreFinder.AnyUsableCoreGlobal(pawn));
            sb.AppendLine("    - CanReach(Touch, Deadly): " + pawn.CanReach(target, PathEndMode.Touch, Danger.Deadly));
            sb.AppendLine("    - IsForbidden / IsBurning: " + target.IsForbidden(pawn) + " / " + target.IsBurning());
            sb.AppendLine("    - CanReserve / 忽略他人预约: " + pawn.CanReserve(target, 1, -1)
                          + " / " + pawn.CanReserve(target, 1, -1, null, true));

            Pawn reserver = map.reservationManager.FirstRespectedReserver(target, pawn);
            if (reserver != null)
                sb.AppendLine("    - 当前被预留: " + reserver.LabelShort);

            bool canConstruct;
            JobFailReason.Clear();
            try
            {
                canConstruct = GenConstruct.CanConstruct(target, pawn, WorkTypeDefOf.Hauling, true,
                    DigitalStorage_JobDefOf.DigitalStorage_WithdrawToConstruction);
            }
            catch (System.Exception ex)
            {
                canConstruct = false;
                sb.AppendLine("    - CanConstruct 抛异常: " + ex.Message);
            }
            sb.AppendLine("    - GenConstruct.CanConstruct(Hauling, forced, 我们的 JobDef): " + canConstruct
                          + " · 理由: " + (JobFailReason.HaveReason ? JobFailReason.Reason.ToString() : "（无）"));

            bool made;
            Job job;
            try
            {
                made = DSConstructionDelivery.TryMakeJob(pawn, c, true, out job);
            }
            catch (System.Exception ex)
            {
                made = false;
                job = null;
                sb.AppendLine("    - TryMakeJob 抛异常: " + ex.Message);
            }
            sb.AppendLine("    - DSConstructionDelivery.TryMakeJob(forced): " + made
                          + (job != null ? " · count=" + job.count : ""));
            if (job != null)
                sb.AppendLine("    ⇒ 产品路径应当能出菜单项；若右键仍没有，看启动日志的补丁类清单里有没有本 provider。");

            Log.Warning(sb.ToString());
        }
    }
}
