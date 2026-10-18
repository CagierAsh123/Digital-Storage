// =====================================================================================
//  【本地新增文件】Digital Storage 本地整合版 —— 上游 main 分支**没有**这个文件。
// -------------------------------------------------------------------------------------
//  本文件是什么：
//    合成 Job 的**状态判定**（照 AE2 的 `CraftingSubmitErrorCode` / `UnsuitableCpus` 那套分类），
//    外加两件 AE2 里必须有、DS 以前没有的东西：
//      · **在途登记**（AE2 的 `waitingFor`）：台子上正在做的产物，算"预计会有"，别当成"已经有了"
//      · **缺料报告**：原生材料合计 − 核心已有 − 在途 = 还缺多少（AE2 的 CraftingReport / craftingreport.png）
//
//  为什么要有它：用户原话「阻塞和带合成的任务放的位置好像还是不对」——
//    面板以前只会说"阻塞/待处理"，玩家看不出**到底卡在哪**。这里把原因分成 AE2 那七类，每类都能直接照做。
//
//  性能：缺料报告要现算一棵合成树（`CraftTree.Build`），**不能每帧算** ⇒ 这里带一个 30 tick 的缓存
//    （和 comp 数产物是同一个节流口径）。
//
//  怎么回退：删掉本文件 + 对应那一组改动（source-changes-groupAJ.ps1）即可。
// =====================================================================================
using System;
using System.Collections.Generic;
using System.Text;
using DigitalStorage.Components;
using DigitalStorage.UI;     // ★ 只为了 AE2Draw 的配色（这个文件本质上是"给界面看的判定"，配色放这儿省得两边各写一套）
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 状态分类 —— **逐条照抄 AE2 的 <c>CraftingSubmitErrorCode</c>**（见 DS-AE2合成逻辑改造规格.md §6.3），
    /// 只多了我们自己的"一切正常"。
    /// </summary>
    internal static class CraftJobCode
    {
        /// <summary>没有异常（合成中 / 已达标 / 已完成）。</summary>
        public const int Ok = 0;

        /// <summary>INCOMPLETE_PLAN：计划本身不完整（某一步没有订单 / 配方已经不可用）。</summary>
        public const int IncompletePlan = 1;

        /// <summary>NO_CPU_FOUND：本图一台制作代理都没有（界面层用，判定入口一般拿得到 comp）。</summary>
        public const int NoCpuFound = 2;

        /// <summary>NO_SUITABLE_CPU_FOUND：代理在，但范围内没有能做这条配方的工作台。</summary>
        public const int NoSuitableCpu = 3;

        /// <summary>CPU_OFFLINE：代理停用 / 断电。</summary>
        public const int CpuOffline = 4;

        /// <summary>CPU_BUSY：能做，但台子都让别的订单占着（排队中）。</summary>
        public const int CpuBusy = 5;

        /// <summary>CPU_TOO_SMALL：计划比代理的合成能力大（M4 引入"合成存储"后启用）。</summary>
        public const int CpuTooSmall = 6;

        /// <summary>MISSING_INGREDIENT：缺原料（附缺料清单）。</summary>
        public const int MissingIngredient = 7;
    }

    /// <summary>缺料报告的一行：某个原生材料"需要 / 核心有 / 在途 / 还缺"。</summary>
    internal sealed class CraftJobMissingRow
    {
        public ThingDef Def;
        public int Need;
        public int InCore;

        /// <summary>在途（台子上正在做、做完会进核心的量）—— AE2 的 <c>waitingFor</c>。</summary>
        public int InFlight;

        public int Short
        {
            get { return Mathf.Max(0, Need - InCore - InFlight); }
        }
    }

    internal static class CraftJobStatus
    {
        // ---- 缺料报告的缓存（每帧算一棵树太贵）----
        private static CraftJob cachedJob;
        private static int cachedTick = -99999;
        private static List<CraftJobMissingRow> cachedRows = new List<CraftJobMissingRow>();

        /// <summary>
        /// 这条 Job 现在是什么状态。<paramref name="text"/> 是可直接显示的一句话（已翻译）。
        ///
        /// <para>判定顺序是**刻意的**：先说"代理自己不能干活"（停用/断电），再说"计划坏了"，
        /// 然后"已经好了"，再"在产"，最后才是"缺料 / 没台子 / 排队"——
        /// 从最外层往里查，玩家照着第一条就能动手。</para>
        /// </summary>
        public static int CodeOf(CompBillAutomation comp, CraftJob job, out string text)
        {
            text = "";
            if (job == null) return CraftJobCode.Ok;

            if (job.state == CraftJobState.Finished)
            {
                text = "DS_JOB_St_Finished".Translate().ToString();
                return CraftJobCode.Ok;
            }
            if (job.state == CraftJobState.Cancelled)
            {
                text = "DS_JOB_St_Cancelled".Translate().ToString();
                return CraftJobCode.Ok;
            }

            if (comp == null)
            {
                text = "DS_JOB_St_NoProxy".Translate().ToString();
                return CraftJobCode.NoCpuFound;
            }
            if (!comp.Enabled)
            {
                text = "DS_JOB_St_Disabled".Translate().ToString();
                return CraftJobCode.CpuOffline;
            }
            if (!comp.Powered)
            {
                text = "DS_JOB_St_NoPower".Translate().ToString();
                return CraftJobCode.CpuOffline;
            }

            // ---- 计划完整性：每一步都得还有订单，而且配方仍然可用 ----
            for (int i = 0; i < job.steps.Count; i++)
            {
                CraftJobStep s = job.steps[i];
                if (s == null || s.recipe == null) continue;
                if (CraftTree.FindPlan(comp, s.recipe) == null)
                {
                    text = "DS_JOB_St_Incomplete".Translate(s.recipe.LabelCap).ToString();
                    return CraftJobCode.IncompletePlan;
                }
            }

            CraftPlan root = CraftTree.FindPlan(comp, job.rootRecipe);
            if (root == null)
            {
                text = "DS_JOB_St_Incomplete".Translate(
                    (job.rootRecipe != null) ? job.rootRecipe.LabelCap.ToString() : "?").ToString();
                return CraftJobCode.IncompletePlan;
            }

            if (CompBillAutomation.StepSatisfied(root))
            {
                text = "DS_JOB_St_Done".Translate().ToString();
                return CraftJobCode.Ok;
            }
            if (HasWork(comp, job))
            {
                text = "DS_JOB_St_Crafting".Translate().ToString();
                return CraftJobCode.Ok;
            }

            // ---- 缺料（最常见，先报它：玩家一眼知道去补什么）----
            List<CraftJobMissingRow> missing = Missing(comp, job);
            if (missing.Count > 0)
            {
                text = "DS_JOB_St_Missing".Translate(ShortSummary(missing)).ToString();
                return CraftJobCode.MissingIngredient;
            }

            // ---- 台子 ----
            if (comp.UsableBenchCountFor(job.rootRecipe) == 0)
            {
                text = "DS_JOB_St_NoBench".Translate(
                    (job.rootRecipe != null) ? job.rootRecipe.LabelCap.ToString() : "?").ToString();
                return CraftJobCode.NoSuitableCpu;
            }

            text = "DS_JOB_St_Busy".Translate().ToString();
            return CraftJobCode.CpuBusy;
        }

        /// <summary>这条 Job 的任一步骤是不是正在干活（= AE2 的 job 处于 CRAFTING）。</summary>
        public static bool HasWork(CompBillAutomation comp, CraftJob job)
        {
            if (comp == null || job == null) return false;
            for (int i = 0; i < job.steps.Count; i++)
            {
                CraftJobStep s = job.steps[i];
                if (s == null || s.recipe == null) continue;
                CraftPlan p = CraftTree.FindPlan(comp, s.recipe);
                if (p == null || p.lines == null) continue;
                for (int k = 0; k < p.lines.Count; k++)
                {
                    if (p.lines[k] != null && p.lines[k].HasWork) return true;
                }
            }
            return false;
        }

        /// <summary>某个产物**在途**多少个（AE2 的 <c>waitingFor</c>）：台子上已经拿到料、正在做的那些。</summary>
        public static int InFlight(CompBillAutomation comp, ThingDef def)
        {
            if (comp == null || def == null) return 0;
            IList<CraftPlan> plans = comp.PlansForReading;
            if (plans == null) return 0;

            int n = 0;
            for (int i = 0; i < plans.Count; i++)
            {
                CraftPlan p = plans[i];
                if (p == null || p.recipe == null || p.recipe.products == null || p.lines == null) continue;

                int per = 0;      // 这条配方一次产出几个 def
                for (int k = 0; k < p.recipe.products.Count; k++)
                {
                    ThingDefCountClass pc = p.recipe.products[k];
                    if (pc != null && pc.thingDef == def) per += Mathf.Max(1, pc.count);
                }
                if (per <= 0) continue;

                for (int k = 0; k < p.lines.Count; k++)
                {
                    CraftLine line = p.lines[k];
                    // 只有"已经取到料、正在做"的才算在途：还没取到料的那些，料还在核心里，
                    // 核心里那份已经被 CoreStock 数过一遍了（数两遍会把缺料算少 ⇒ 报"齐了"却开不了工）。
                    if (line != null && line.HasWork) n += per;
                }
            }
            return n;
        }

        /// <summary>
        /// 缺料报告（带 30 tick 缓存）。**只报固定原料**：可自选材质的槽位由订单上的材料设置决定，
        /// 这里报"木材/钢铁/…"没有意义（那是 CraftTree 的 stuffAgg 干的事）。
        /// </summary>
        public static List<CraftJobMissingRow> Missing(CompBillAutomation comp, CraftJob job)
        {
            int now = (Find.TickManager != null) ? Find.TickManager.TicksGame : 0;
            if (job == cachedJob && now - cachedTick < 30 && now >= cachedTick) return cachedRows;

            cachedJob = job;
            cachedTick = now;
            cachedRows = ComputeMissing(comp, job);
            return cachedRows;
        }

        private static List<CraftJobMissingRow> ComputeMissing(CompBillAutomation comp, CraftJob job)
        {
            List<CraftJobMissingRow> rows = new List<CraftJobMissingRow>();
            if (comp == null || job == null || job.rootRecipe == null || job.rootProduct == null) return rows;

            Map map = (comp.parent != null) ? comp.parent.Map : null;
            if (map == null) return rows;

            CraftTreeNode tree;
            try
            {
                tree = CraftTree.Build(comp, job.rootRecipe, job.rootProduct, job.wanted);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 合成状态：算缺料时建树失败（本次不报缺料）：" + e, 553311);
                return rows;
            }
            if (tree == null) return rows;

            Dictionary<ThingDef, int> stock = CraftTree.CoreStock(map);
            List<CraftTreeLeaf> leaves = CraftTree.CollectRawMaterials(tree, stock);
            for (int i = 0; i < leaves.Count; i++)
            {
                CraftTreeLeaf leaf = leaves[i];
                if (leaf == null || leaf.IsStuffSlot || leaf.Def == null) continue;

                CraftJobMissingRow row = new CraftJobMissingRow();
                row.Def = leaf.Def;
                row.Need = Mathf.Max(0, leaf.Count);
                row.InCore = Mathf.Max(0, leaf.InCore);
                row.InFlight = InFlight(comp, leaf.Def);
                if (row.Short > 0) rows.Add(row);
            }
            rows.Sort(delegate (CraftJobMissingRow a, CraftJobMissingRow b) { return b.Short - a.Short; });
            return rows;
        }

        /// <summary>"钢铁 ×12、零部件 ×3"这样一句话摘要（最多两项 + "…"）。</summary>
        public static string ShortSummary(List<CraftJobMissingRow> rows)
        {
            if (rows == null || rows.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            int n = Mathf.Min(2, rows.Count);
            for (int i = 0; i < n; i++)
            {
                if (i > 0) sb.Append("、");
                sb.Append(rows[i].Def.LabelCap).Append(" ×").Append(rows[i].Short);
            }
            if (rows.Count > n) sb.Append(" …");
            return sb.ToString();
        }

        /// <summary>某一步的状态文字（步骤行右侧那一列）。</summary>
        public static string StepText(CompBillAutomation comp, CraftJob job, CraftJobStep step)
        {
            if (step == null) return "";
            CraftPlan plan = (comp != null && step.recipe != null) ? CraftTree.FindPlan(comp, step.recipe) : null;
            if (plan == null) return "DS_JOB_St_Incomplete".Translate(
                (step.recipe != null) ? step.recipe.LabelCap.ToString() : "?").ToString();
            if (CompBillAutomation.StepSatisfied(plan)) return "DS_JOB_St_Done".Translate().ToString();

            if (plan.lines != null)
            {
                for (int i = 0; i < plan.lines.Count; i++)
                {
                    CraftLine line = plan.lines[i];
                    if (line == null) continue;
                    if (line.HasWork) return "DS_JOB_St_Crafting".Translate().ToString();
                    if (!line.BlockKey.NullOrEmpty()) return line.BlockKey.Translate().ToString();
                }
            }
            return (plan.lines != null && plan.lines.Count > 0)
                ? "DS_CA_Waiting".Translate().ToString()
                : "DS_JOB_St_Queue".Translate().ToString();
        }

        /// <summary>进度（0..1）与角标。次数模式看"已做/总量"，其余看"核心里有/目标"。</summary>
        public static void Progress(CompBillAutomation comp, CraftJob job,
            Dictionary<ThingDef, int> stock, out float pct, out string tag)
        {
            pct = 0f;
            tag = "";
            if (comp == null || job == null) return;

            CraftPlan root = CraftTree.FindPlan(comp, job.rootRecipe);
            if (root != null && root.mode == CraftPlan.ModeCount)
            {
                int done = Mathf.Max(0, root.completed);
                int total = done + Mathf.Max(0, root.remaining);
                pct = (total > 0) ? Mathf.Clamp01(done / (float)total) : 1f;
                tag = done + "/" + total;
                return;
            }

            int have = 0;
            if (job.rootProduct != null && stock != null) stock.TryGetValue(job.rootProduct, out have);
            int need = Mathf.Max(1, job.wanted);
            int cur = Mathf.Max(have, (root != null) ? root.countedCount : 0);
            pct = Mathf.Clamp01(cur / (float)need);
            tag = cur + "/" + need;
        }

        /// <summary>本图所有制作代理（= AE2 的 CPU 列表）。下单窗的"交由哪台"与状态窗共用这一份口径。</summary>
        public static List<CompBillAutomation> AllProxies(Map map)
        {
            List<CompBillAutomation> list = new List<CompBillAutomation>();
            if (map == null) return list;
            List<Building> all = map.listerBuildings.allBuildingsColonist;
            for (int i = 0; i < all.Count; i++)
            {
                CompBillAutomation c = all[i].GetComp<CompBillAutomation>();
                if (c != null) list.Add(c);
            }
            return list;
        }

        /// <summary>
        /// 这条 Job 的"计划规模"（照 AE2 的 **bytes**：计划里所有件数之和）。
        /// 合成 CPU 的合成存储装不下它时就该拒绝（<see cref="CraftJobCode.CpuTooSmall"/>）。
        /// </summary>
        public static int PlanBytesOf(CraftJob job)
        {
            if (job == null || job.steps == null) return 0;
            int n = 0;
            for (int i = 0; i < job.steps.Count; i++)
            {
                if (job.steps[i] != null) n += Mathf.Max(0, job.steps[i].needCount);
            }
            return n;
        }

        /// <summary>
        /// <b>自检报告</b>（AE2 的 Network Status / Crafting Monitor 的对应物）：
        /// 把"这台代理现在到底是什么状态"写成一段可读文本，供 gizmo 打进 <c>Player.log</c>。
        ///
        /// <para>为什么要它：Job 模型 / 状态窗 / 容量这些改动**没法在开发机上跑真机**，
        /// 玩家第一次进游戏时，一条日志就能代替来回问答（Agent 读得到 <c>Player.log</c>，读不到你的屏幕）。</para>
        /// </summary>
        public static string BuildReport(CompBillAutomation comp)
        {
            StringBuilder sb = new StringBuilder();
            if (comp == null) return "[合成自检] comp = null";

            int lineCount = 0;
            int planCount = 0;
            IList<CraftPlan> allPlans = comp.PlansForReading;
            if (allPlans != null)
            {
                planCount = allPlans.Count;
                for (int i = 0; i < allPlans.Count; i++)
                {
                    if (allPlans[i] != null && allPlans[i].lines != null) lineCount += allPlans[i].lines.Count;
                }
            }

            sb.Append("[合成自检] 代理=").Append((comp.parent != null) ? comp.parent.LabelShort : "?");
            sb.Append(" 电力=").Append(comp.Powered ? "有" : "无").Append('/').Append(comp.Enabled ? "启用" : "停用");
            sb.Append(" 耗电=").Append(comp.CurrentWatts.ToString("#####0")).Append("W");
            sb.Append(" 产线=").Append(lineCount).Append('/').Append(planCount).Append(" 单");
            sb.Append(" 合成=").Append((comp.JobsForReading == null) ? 0 : comp.JobsForReading.Count);
            sb.Append(" 存储=").Append(comp.StorageTierLabel()).Append('(').Append(comp.StorageBytes).Append(" 件)");
            sb.Append(" 并行处理单元=").Append(comp.ParallelUnits).Append("(每步最多 ").Append(comp.ParallelPerStep).Append(" 张台)");
            sb.AppendLine();

            IList<CraftJob> jobs = comp.JobsForReading;
            if (jobs == null || jobs.Count == 0)
            {
                sb.AppendLine("  （这台代理还没有合成请求）");
                return sb.ToString();
            }

            for (int i = 0; i < jobs.Count && i < 12; i++)
            {
                CraftJob job = jobs[i];
                if (job == null) continue;

                string status;
                int code = CodeOf(comp, job, out status);
                float pct;
                string tag;
                Progress(comp, job, CraftTree.CoreStock((comp.parent != null) ? comp.parent.Map : null), out pct, out tag);

                sb.Append("  合成#").Append(i + 1).Append(' ')
                  .Append((job.rootRecipe != null) ? job.rootRecipe.defName : "?")
                  .Append(" 状态=").Append(StatusNameOf(code)).Append("（").Append(status).Append("）")
                  .Append(" 进度=").Append(tag)
                  .Append(" 步骤=").Append((job.steps == null) ? 0 : job.steps.Count)
                  .Append(" 规模=").Append(PlanBytesOf(job)).Append('/').Append(comp.StorageBytes);
                sb.AppendLine();

                if (job.steps != null)
                {
                    for (int k = 0; k < job.steps.Count && k < 16; k++)
                    {
                        CraftJobStep s = job.steps[k];
                        if (s == null || s.recipe == null) continue;
                        CraftPlan p = CraftTree.FindPlan(comp, s.recipe);
                        sb.Append("      ").Append(s.IsRoot ? "[根] " : "     ")
                          .Append(s.recipe.defName).Append(" ×").Append(s.needCount)
                          .Append("（做 ").Append(s.crafts).Append(" 次，深度 ").Append(s.depth).Append("）")
                          .Append(" 订单=").Append((p == null) ? "无" : (p.mode + "/" + p.lines.Count + " 线"))
                          .Append(" 步骤状态=").Append(StepText(comp, job, s));
                        sb.AppendLine();
                    }
                }

                List<CraftJobMissingRow> miss = Missing(comp, job);
                if (miss.Count == 0)
                {
                    sb.AppendLine("      缺料：无");
                }
                else
                {
                    sb.Append("      缺料：");
                    for (int k = 0; k < miss.Count && k < 10; k++)
                    {
                        if (k > 0) sb.Append('、');
                        sb.Append(miss[k].Def.defName).Append(" 需要").Append(miss[k].Need)
                          .Append("/核心").Append(miss[k].InCore).Append("/在途").Append(miss[k].InFlight)
                          .Append("/缺").Append(miss[k].Short);
                    }
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        /// <summary>把状态码翻成英文名（日志里用；界面用的是 keyed 文案）。</summary>
        public static string StatusNameOf(int code)
        {
            switch (code)
            {
                case CraftJobCode.IncompletePlan: return "INCOMPLETE_PLAN";
                case CraftJobCode.NoCpuFound: return "NO_CPU_FOUND";
                case CraftJobCode.NoSuitableCpu: return "NO_SUITABLE_CPU_FOUND";
                case CraftJobCode.CpuOffline: return "CPU_OFFLINE";
                case CraftJobCode.CpuBusy: return "CPU_BUSY";
                case CraftJobCode.CpuTooSmall: return "CPU_TOO_SMALL";
                case CraftJobCode.MissingIngredient: return "MISSING_INGREDIENT";
                default: return "OK";
            }
        }

        /// <summary>状态色：正常=青 / 缺料=红 / 没台子=红 / 断电停用=灰 / 排队=黄。</summary>
        public static Color ColorOf(int code)
        {
            switch (code)
            {
                case CraftJobCode.MissingIngredient:
                case CraftJobCode.NoSuitableCpu:
                case CraftJobCode.IncompletePlan:
                    return AE2Draw.Bad;
                case CraftJobCode.CpuOffline:
                    return AE2Draw.TextDimCol;
                case CraftJobCode.CpuBusy:
                    return AE2Draw.Warn;
                default:
                    return AE2Draw.Accent;
            }
        }
    }
}
