using System.Collections.Generic;
using System.Text;
using DigitalStorage.AI;
using DigitalStorage.Core;
using RimWorld;
using Verse;
using Verse.AI;

namespace DigitalStorage.UI
{
    /// <summary>
    /// 右键蓝图 / 框 →「用数字存储的材料建造」。
    ///
    /// <para><b>为什么需要这条路径</b>（用户 2026-10-18 上报：地上没材料时右键蓝图没有任何反应）：
    /// 原版右键强制工作挂在 <c>FloatMenuOptionProvider_WorkGivers</c> 上，而它有三道无声闸门 ——
    /// ①<c>:70</c> <c>pawn.thinker.TryGetMainTreeThinkNode&lt;JobGiver_Work&gt;() == null</c> 直接 <c>yield break</c>；
    /// ②<c>:127-130</c> <c>HasJobOnThing</c> 返回 false 且 <c>JobFailReason</c> 没理由时**连菜单项都不给**；
    /// ③原版送货 giver 自己先过 <c>GenConstruct.CanGetResources_NewTemp</c>
    /// （<c>WorkGiver_ConstructDeliverResourcesToBlueprints.cs:52</c>），它只认**地图上物理存在**的材料
    /// （<c>GenConstruct.cs:129-137</c> 的 <c>ItemAvailability.ThingsAvailableAnywhere</c>），
    /// 核心里有多少都不算。于是"材料全在核心、地上空着"时右键就是一片空白。</para>
    ///
    /// <para><b>这条路径怎么绕开</b>：不碰原版 giver，自己算一遍
    /// <see cref="DSConstructionDelivery"/>（与本 mod 的 WorkGiver 同源）——
    /// 有料就给可点项（<c>playerForced</c> 作业），没料也给一项**禁用的、带理由 tooltip 的**，
    /// 把"没反应"变成"看得到为什么"。</para>
    ///
    /// <para><b>与 WorkGiver 的关系</b>：两条路都可能给出一项（本 giver 的
    /// <c>WorkGiverDef</c> 是 <c>directOrderable</c>）。内容一致、都是强制作业，重复一项不影响正确性；
    /// 想知道原件为什么没出，用 Dev 调试动作「建造取料：这件蓝图为什么没有取料选项」逐道门打印。</para>
    /// </summary>
    public class FloatMenuOptionProvider_DS_SupplyConstruct : FloatMenuOptionProvider
    {
        protected override bool Drafted => false;
        protected override bool Undrafted => true;
        protected override bool Multiselect => false;
        protected override bool RequiresManipulation => true;
        protected override bool MechanoidCanDo => true;

        protected override FloatMenuOption GetSingleOptionFor(Thing clickedThing, FloatMenuContext context)
        {
            FloatMenuOption none = null;
            try
            {
                none = BuildOption(clickedThing, context);
            }
            catch (System.Exception ex)
            {
                // FloatMenuOptionProvider 的异常会打断整个右键菜单，这里吞掉并留一次日志。
                Log.ErrorOnce("[DigitalStorage] SupplyConstruct 右键项构造失败: " + ex,
                    clickedThing.thingIDNumber ^ 0x44534D43);
            }
            return none;
        }

        private static FloatMenuOption BuildOption(Thing clickedThing, FloatMenuContext context)
        {
            Pawn pawn = context.FirstSelectedPawn;
            if (pawn == null || !pawn.Spawned || pawn.Map == null) return null;

            // 只接管"建造目标"：蓝图 / 框。安装蓝图没有材料账单，原版自己处理。
            if (!(clickedThing is Blueprint) && !(clickedThing is Frame)) return null;
            if (clickedThing is Blueprint_Install) return null;
            if (!clickedThing.Spawned || clickedThing.Map != pawn.Map) return null;
            if (clickedThing.Faction != pawn.Faction) return null;
            if (clickedThing.IsBurning()) return null;
            if (clickedThing.IsForbidden(pawn)) return null;
            if (!(clickedThing is IConstructible c)) return null;
            if (!NeedsMaterials(c)) return null;                       // 料已送齐 → 交给原版"建造"
            if (!CoreFinder.AnyUsableCoreGlobal(pawn)) return null;     // 没有可用核心 → 不归我们管
            if (!pawn.CanReach(clickedThing, PathEndMode.Touch, Danger.Deadly)) return null;

            if (DSConstructionDelivery.TryMakeJob(pawn, c, forced: true, out Job job) && job != null)
            {
                FloatMenuOption option = new FloatMenuOption(
                    "DS_FM_SupplyConstruct".Translate(clickedThing.LabelShort).CapitalizeFirst(),
                    delegate { pawn.jobs.TryTakeOrderedJob(job, JobTag.MiscWork); },
                    MenuOptionPriority.Default, null, clickedThing);
                return FloatMenuUtility.DecoratePrioritizedTask(option, pawn, clickedThing);
            }

            // 有核心、目标也还在等料，但规划不出作业：给一项禁用项，把原因写在 tooltip 里。
            FloatMenuOption blocked = new FloatMenuOption(
                "DS_FM_SupplyConstructBlocked".Translate().CapitalizeFirst(),
                null, MenuOptionPriority.Default, null, clickedThing);
            blocked.tooltip = new TipSignal(DescribeWhyBlocked(c, clickedThing, pawn));
            return blocked;
        }

        /// <summary>这件建造目标是否还缺料（只看账单，不看料在哪）。</summary>
        private static bool NeedsMaterials(IConstructible c)
        {
            try
            {
                List<ThingDefCountClass> cost = c.TotalMaterialCost();
                if (cost == null || cost.Count == 0) return false;
                for (int i = 0; i < cost.Count; i++)
                {
                    ThingDefCountClass m = cost[i];
                    if (m?.thingDef == null || m.count <= 0) continue;
                    if (c.ThingCountNeeded(m.thingDef) > 0) return true;
                }
                return false;
            }
            catch
            {
                return false; // 第三方 IConstructible 账单会抛；原版路径兜底
            }
        }

        /// <summary>没出可点项时，逐条解释：缺多少料 / 被谁预留 / 被什么挡住。</summary>
        private static string DescribeWhyBlocked(IConstructible c, Thing target, Pawn pawn)
        {
            var sb = new StringBuilder();

            // ① 逐材料算"还缺多少"：本图 + 其它图核心一起算（与 ConstructMaterialPlanner 同源）
            var missing = new List<string>();
            try
            {
                List<ThingDefCountClass> cost = c.TotalMaterialCost();
                if (cost != null && cost.Count > 0)
                {
                    var have = new Dictionary<ThingDef, int>();
                    var all = new List<Thing>();
                    HaulSourceContents.GatherAllIncludingRemote(pawn.Map, all);
                    for (int i = 0; i < all.Count; i++)
                    {
                        Thing t = all[i];
                        if (t?.def == null || t.Destroyed) continue;
                        have.TryGetValue(t.def, out int cur);
                        have[t.def] = cur + t.stackCount;
                    }
                    for (int i = 0; i < cost.Count; i++)
                    {
                        ThingDefCountClass m = cost[i];
                        if (m?.thingDef == null || m.count <= 0) continue;
                        int need = c.ThingCountNeeded(m.thingDef);
                        if (need <= 0) continue;
                        have.TryGetValue(m.thingDef, out int got);
                        if (got < need)
                            missing.Add(m.thingDef.LabelCap + " " + (need - got));
                    }
                }
            }
            catch (System.Exception ex)
            {
                Log.WarningOnce("[DigitalStorage] 取料诊断失败: " + ex.Message, 0x44534D44);
            }

            if (missing.Count > 0)
                sb.Append("DS_FM_SupplyShort".Translate(GenText.ToCommaList(missing, true)));
            else
                sb.Append("DS_FM_SupplyReserved".Translate());

            // ② 目标本身被别的作业订着（"ReservedBy" 是原版翻译键，参数 = 小人名, 小人）
            Pawn reserver = pawn.Map?.reservationManager?.FirstRespectedReserver(target, pawn);
            if (reserver != null)
                sb.Append("\n").Append("ReservedBy".Translate(reserver.LabelShort, reserver));

            return sb.ToString();
        }
    }
}
