using System;
using System.Collections.Generic;
using System.Text;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// <b>配方展开（一步成型）</b> —— 让核心补上「中间产物不在核心里」这块空白。
    ///
    /// <para><b>为什么需要</b>：取料完全委托原版 <c>WorkGiver_DoBill.JobOnThing</c>（三个唯一漏斗的原则），
    /// 原版只认「配方原料本身在容器里」。于是「制作超凡仿生眼」只要缺<b>高级零部件</b>就永远报
    /// <c>DS_BA_Block_Material</c> 等死 —— 哪怕核心里精密零部件和钢都是满的。用户报障原文：
    /// 「这次我们要加上存在中间产物配方的支持」。</para>
    ///
    /// <para><b>做法（用户拍板方案 A：配方展开，2026-10-18）</b>：只在原版漏斗报缺料时登场。
    /// 把「缺失 + 有固定 def + 我们自己的台子能自产」的中间件递归替换成它的原料，最终仍然只走
    /// 一次 <c>GenRecipe.MakeRecipeProducts</c>（不真的造中间件、不占第二张台子、不占第二格产线槽），
    /// 工作量 = 最终配方工作量 + Σ(中间件配方工作量 × 次数 × <see cref="Settings.DigitalStorageSettings.craftExpandWorkFactor"/>）。
    /// 中途不产出、不扣料；扣料与结算仍在 <see cref="BillCraftFunnel.TryComplete"/> 那一刻统一发生。</para>
    ///
    /// <para><b>为什么不做方案 B（子订单链）</b>：子计划要额外占一格产线槽 + 一张台子，而主计划正占着
    /// 台子等料（<c>StillValid</c> 要求台子还在）—— 产线槽少的时候会死锁。</para>
    ///
    /// <para><b>硬边界</b>（每条都有代价，写清楚免得以后踩）：
    /// <list type="number">
    /// <item><b>只展开固定材料</b>（<c>IngredientCount.IsFixedIngredient</c>）。带材料类别滤镜
    ///   （衣物/家具的 stuff）和按营养计价（食物）的原料没法脱离实物算出"需要几个"，
    ///   一律不展开；这类配方只能等原版漏斗，行为与改动前一致。</item>
    /// <item><b>中间件必须能在我们扫到的台子上做</b>（<see cref="CraftUnlocks.For"/> 里出现），
    ///   否则宁可失败也不"凭空变出来" —— 玩家没建机械加工台就别想展开零部件。</item>
    /// <item><b>深度上限</b> <see cref="Settings.DigitalStorageSettings.craftExpandDepth"/>：1 = 只拆直接中间件，
    ///   2 = 拆出来的东西若本身也是中间件再拆一层，3 = 再一层。超过上限就放弃展开、退回原来的缺料状态。</item>
    /// <item><b>成环检测</b>：展开链路上同一配方第二次出现即失败（mod 里「A 要 B、B 要 A」并不罕见）。</item>
    /// <item><b>只从核心取料</b>：与 <see cref="BillCraftFunnel"/> 的守卫一致，地面上的料不参与。</item>
    /// </list></para>
    /// </summary>
    internal static class RecipeExpansion
    {
        /// <summary>展开链路最长节点数（防御 mod 里超长的多级链，正常配方 3~4 层就到底了）。</summary>
        private const int MaxNodes = 24;

        /// <summary>一次展开尝试的工作集。只在 <see cref="TryAcquire"/> 内活一次，不做缓存。</summary>
        private sealed class Ctx
        {
            public Map map;
            public Thing bench;
            public CraftLine line;
            public RecipeDef finalRecipe;
            public Bill finalBill;
            public HashSet<ThingDef> benchDefs;
            public int maxDepth;
            public float factor;

            /// <summary>def → 核心里**还没被本轮占用**的总件数（展开时边算边扣）。</summary>
            public Dictionary<ThingDef, int> avail = new Dictionary<ThingDef, int>();

            /// <summary>def → 展开后的总需求量（可以重复累加）。</summary>
            public Dictionary<ThingDef, int> need = new Dictionary<ThingDef, int>();

            /// <summary>需求的稳定顺序（诊断日志、面板提示用）。</summary>
            public List<ThingDef> order = new List<ThingDef>();

            /// <summary>def → 核心里的实物堆（按 stackCount 降序，取料时优先整堆）。</summary>
            public Dictionary<ThingDef, List<Thing>> stacks = new Dictionary<ThingDef, List<Thing>>();

            public List<RecipeDef> path = new List<RecipeDef>();
            public Dictionary<RecipeDef, Bill> bills = new Dictionary<RecipeDef, Bill>();
            public Dictionary<ThingDef, RecipeDef> producers = new Dictionary<ThingDef, RecipeDef>();
            public float extraWork;
            public List<string> trail = new List<string>();
        }

        /// <summary>
        /// 给一条线用「展开」的方式取活。返回 true 表示 <c>line</c> 的
        /// <c>Ingredients/Counts/WorkAmount/BaseRate</c> 已就绪（调用方按普通取活成功处理）。
        ///
        /// <para>⚠️ 必须在 <c>BillProbe</c> 的 <c>DigitalWorkerScope</c> 作用域内调用：
        /// 里面要算 <c>w.GetStatValue</c>，假 pawn 出作用域会因为没有 Map 而炸。</para>
        /// </summary>
        public static bool TryAcquire(CompBillAutomation comp, CraftPlan plan, CraftLine line, Map map, Pawn w, int now, Thing bench)
        {
            try
            {
                return TryAcquireCore(comp, plan, line, map, w, now, bench);
            }
            catch (Exception e)
            {
                // 展开只是兜底尝试：任何意外都不能把异常抛进原版工作扫描的调用栈
                Log.ErrorOnce("[DigitalStorage] 配方展开异常（本轮放弃，按缺料处理）：" + e, 771139);
                if (line != null)
                {
                    CraftIngredientClaims.Release(line);
                    line.Expanded = false;
                    line.ExpandNote = null;
                }
                return false;
            }
        }

        private static bool TryAcquireCore(CompBillAutomation comp, CraftPlan plan, CraftLine line, Map map, Pawn w, int now, Thing bench)
        {
            if (comp == null || plan == null || plan.recipe == null || line == null) return false;
            if (map == null || bench == null || w == null) return false;

            int depth = Mathf.Clamp(DigitalStorage.Settings.DigitalStorageSettings.craftExpandDepth, 1, 3);
            float factor = Mathf.Clamp01(DigitalStorage.Settings.DigitalStorageSettings.craftExpandWorkFactor);

            Ctx ctx = new Ctx();
            ctx.map = map;
            ctx.bench = bench;
            ctx.line = line;
            ctx.finalRecipe = plan.recipe;
            ctx.finalBill = line.Bill;
            ctx.benchDefs = BenchDefs(comp);
            ctx.maxDepth = depth;
            ctx.factor = factor;

            if (!FillAvail(ctx)) return false;

            string fail;
            if (!Accumulate(ctx, plan.recipe, 1, depth, out fail))
            {
                line.ExpandNote = fail;
                return false;
            }
            if (ctx.need.Count == 0)
            {
                line.ExpandNote = "展开后什么都不缺（不该发生）";
                return false;
            }

            Thing[] things;
            int[] counts;
            if (!Collect(ctx, out things, out counts))
            {
                // 认领在 Collect 内部已经按 line 逐个拿走，失败必须整批退回
                CraftIngredientClaims.Release(line);
                line.ExpandNote = "展开后的原料在核心里凑不齐";
                return false;
            }

            // 先落位再算价（与 BillProbe 同一个理由：算价抛异常时 ClearWork 才有东西可退）
            line.Probe = null;
            line.Ingredients = things;
            line.Counts = counts;
            line.Expanded = true;
            line.ExpandNote = Describe(ctx);

            try
            {
                line.BaseRate = BillProbe.ComputeBaseRate(plan.recipe, bench, w);
                Thing lastIngredient = things.Length > 0 ? things[things.Length - 1] : null;
                line.WorkAmount = line.Bill.GetWorkAmount(lastIngredient) + ctx.extraWork;
                line.WorkLeft = line.WorkAmount;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] 配方展开估价失败（本轮作废，原料认领已退回）：" + e, 771137);
                line.ClearWork();
                line.BlockKey = "DS_BA_NoBill";
                line.NextAcquireTick = now + BillProbe.RetryTicks;
                return false;
            }

            line.NextAcquireTick = 0;
            line.BlockKey = null;
            Performance.DevDrawProfiler.Bump("账单取活", 1);

            try
            {
                line.Bill.Notify_DoBillStarted(w);
                line.Bill.Notify_BillWorkStarted(w);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[DigitalStorage] bill.Notify_* 抛异常（已忽略）：" + e, 771133);
            }
            return true;
        }

        // ===================================================================
        // 一、把「核心里现有多少」摊平成 def → 件数 + 实物堆
        // ===================================================================

        private static bool FillAvail(Ctx ctx)
        {
            List<Thing> pool = new List<Thing>();
            HaulSourceContents.CollectDirect(ctx.map, pool);
            for (int i = 0; i < pool.Count; i++)
            {
                Thing t = pool[i];
                if (t == null || t.Destroyed || t.def == null || t.stackCount <= 0) continue;

                // 只认**本图核心**（背包里的东西 ParentHolder 是 pawn，会在这里被挡掉；
                // BillCraftFunnel 的扣料守卫同样要求核心里，这里不一致会在结算时炸）
                Building_StorageCore core = t.ParentHolder as Building_StorageCore;
                if (core == null || core.Map != ctx.map) continue;

                int n;
                ctx.avail.TryGetValue(t.def, out n);
                ctx.avail[t.def] = n + t.stackCount;

                List<Thing> list;
                if (!ctx.stacks.TryGetValue(t.def, out list))
                {
                    list = new List<Thing>();
                    ctx.stacks[t.def] = list;
                }
                list.Add(t);
            }

            // 大堆优先：一堆积对应一条原料，少一次 SplitOff
            foreach (KeyValuePair<ThingDef, List<Thing>> kv in ctx.stacks)
            {
                kv.Value.Sort((a, b) => b.stackCount.CompareTo(a.stackCount));
            }
            return ctx.avail.Count > 0;
        }

        private static HashSet<ThingDef> BenchDefs(CompBillAutomation comp)
        {
            HashSet<ThingDef> set = new HashSet<ThingDef>();
            IList<Thing> benches = comp.BenchesForReading;
            if (benches == null) return set;
            for (int i = 0; i < benches.Count; i++)
            {
                Thing b = benches[i];
                if (b != null && b.def != null) set.Add(b.def);
            }
            return set;
        }

        // ===================================================================
        // 二、递归摊平需求（不碰实物，只算 def/件数）
        // ===================================================================

        private static bool Accumulate(Ctx ctx, RecipeDef recipe, int batches, int depthLeft, out string fail)
        {
            fail = null;
            if (recipe == null || batches <= 0) return true;
            if (ctx.path.Count >= MaxNodes)
            {
                fail = "展开链路超过 " + MaxNodes + " 层";
                return false;
            }

            ctx.path.Add(recipe);
            try
            {
                for (int i = 0; i < recipe.ingredients.Count; i++)
                {
                    IngredientCount ing = recipe.ingredients[i];
                    if (ing == null || !ing.IsFixedIngredient || ing.FixedIngredient == null)
                    {
                        fail = recipe.LabelCap + " 用了材料类别/计价材料（不支持展开）";
                        return false;
                    }

                    ThingDef def = ing.FixedIngredient;
                    int per = CountRequired(ing, recipe, ctx);
                    if (per <= 0) continue;

                    long total = (long)per * batches;
                    int want = total > int.MaxValue / 4 ? int.MaxValue / 4 : (int)total;

                    int have = Math.Min(want, AvailOf(ctx, def));
                    if (have > 0)
                    {
                        AddNeed(ctx, def, have);
                        ctx.avail[def] = AvailOf(ctx, def) - have;
                    }

                    int miss = want - have;
                    if (miss <= 0) continue;

                    // 材料类原料（stuff）绝不展开。产物 MadeFromStuff 时，
                    // BillCraftFunnel.DominantIngredient 从实际原料里挑 stuff 决定产物材质与颜色；
                    // 把 stuff 摊成别的原料后 ingredients.Where(x => x.def.IsStuff) 可能是空序列，
                    // RandomElementByWeight 会直接抛异常。缺就老老实实报缺料。
                    if (def.IsStuff)
                    {
                        fail = "缺材料类别原料 " + def.LabelCap + "（不展开）";
                        return false;
                    }

                    if (depthLeft <= 0)
                    {
                        fail = "展开深度用完（还缺 " + def.LabelCap + " ×" + miss + "）";
                        return false;
                    }

                    RecipeDef producer = FindProducer(ctx, def);
                    if (producer == null)
                    {
                        fail = "找不到能做 " + def.LabelCap + " 的已解锁配方（缺 ×" + miss + "）";
                        return false;
                    }
                    if (ctx.path.Contains(producer))
                    {
                        fail = "配方成环：" + producer.LabelCap;
                        return false;
                    }

                    int yield = YieldOf(producer, def);
                    if (yield <= 0)
                    {
                        fail = producer.LabelCap + " 的产物里没有 " + def.LabelCap;
                        return false;
                    }

                    int runs = (miss + yield - 1) / yield;
                    ctx.extraWork += producer.WorkAmountTotal(null) * (float)runs * ctx.factor;
                    ctx.trail.Add(def.defName + "×" + miss + " ← " + producer.defName + "×" + runs);

                    if (!Accumulate(ctx, producer, runs, depthLeft - 1, out fail)) return false;
                }
                return true;
            }
            finally
            {
                ctx.path.RemoveAt(ctx.path.Count - 1);
            }
        }

        private static int AvailOf(Ctx ctx, ThingDef def)
        {
            int n;
            return ctx.avail.TryGetValue(def, out n) ? n : 0;
        }

        private static void AddNeed(Ctx ctx, ThingDef def, int n)
        {
            if (n <= 0) return;
            int cur;
            if (ctx.need.TryGetValue(def, out cur))
            {
                ctx.need[def] = cur + n;
                return;
            }
            ctx.need[def] = n;
            ctx.order.Add(def);
        }

        /// <summary>这一份原料要几个（原版口径：<c>IngredientCount.CountRequiredOfFor</c>，会走 ValuePerUnitOf）。</summary>
        private static int CountRequired(IngredientCount ing, RecipeDef recipe, Ctx ctx)
        {
            Bill bill;
            if (ReferenceEquals(recipe, ctx.finalRecipe))
            {
                bill = ctx.finalBill;
            }
            else
            {
                if (!ctx.bills.TryGetValue(recipe, out bill))
                {
                    Bill_Production tmp = new Bill_Production(recipe);
                    tmp.repeatMode = BillRepeatModeDefOf.Forever;
                    ctx.bills[recipe] = tmp;
                    bill = tmp;
                }
            }

            try
            {
                int n = ing.CountRequiredOfFor(ing.FixedIngredient, recipe, bill);
                return n < 0 ? 0 : n;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>某配方一次产出多少个 <paramref name="def"/>（多产物配方只算这一种）。</summary>
        private static int YieldOf(RecipeDef producer, ThingDef def)
        {
            if (producer == null || producer.products == null) return 0;
            int n = 0;
            for (int i = 0; i < producer.products.Count; i++)
            {
                ThingDefCountClass p = producer.products[i];
                if (p != null && p.thingDef == def) n += p.count;
            }
            return n;
        }

        /// <summary>
        /// 找一条「能在我们扫到的台子上做、研究已解锁、不用未完成品」的生产配方。
        /// 只认固定材料的配方 —— 与展开本身的边界一致。
        /// </summary>
        private static RecipeDef FindProducer(Ctx ctx, ThingDef def)
        {
            RecipeDef cached;
            if (ctx.producers.TryGetValue(def, out cached)) return cached;

            RecipeDef best = null;
            List<RecipeDef> all = DefDatabase<RecipeDef>.AllDefsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                RecipeDef r = all[i];
                if (r == null || r == ctx.finalRecipe) continue;
                if (r.UsesUnfinishedThing || r.IsSurgery) continue;
                if (!r.AvailableNow) continue;
                if (r.ingredients == null || r.ingredients.Count == 0) continue;
                if (YieldOf(r, def) <= 0) continue;
                if (!FixedIngredientsOnly(r)) continue;
                if (!CraftableHere(ctx, r)) continue;

                float mine = r.WorkAmountTotal(null);
                float theirs = best == null ? 0f : best.WorkAmountTotal(null);
                if (best == null || mine < theirs
                    || (mine == theirs && string.Compare(r.defName, best.defName, StringComparison.Ordinal) < 0))
                {
                    best = r;
                }
            }

            ctx.producers[def] = best;
            return best;
        }

        private static bool FixedIngredientsOnly(RecipeDef r)
        {
            for (int i = 0; i < r.ingredients.Count; i++)
            {
                IngredientCount ing = r.ingredients[i];
                if (ing == null || !ing.IsFixedIngredient || ing.FixedIngredient == null) return false;
            }
            return true;
        }

        /// <summary>这条配方在不在「扫描到的台子类型」能做的清单里（复用 <see cref="CraftUnlocks"/> 的缓存）。</summary>
        private static bool CraftableHere(Ctx ctx, RecipeDef r)
        {
            if (ctx.benchDefs == null || ctx.benchDefs.Count == 0) return false;
            foreach (ThingDef bd in ctx.benchDefs)
            {
                List<RecipeDef> rs = CraftUnlocks.For(bd);
                if (rs == null) continue;
                for (int i = 0; i < rs.Count; i++)
                {
                    if (ReferenceEquals(rs[i], r)) return true;
                }
            }
            return false;
        }

        // ===================================================================
        // 三、按需求从核心里挑实物并认领
        // ===================================================================

        private static bool Collect(Ctx ctx, out Thing[] things, out int[] counts)
        {
            List<Thing> picked = new List<Thing>();
            List<int> nums = new List<int>();

            for (int i = 0; i < ctx.order.Count; i++)
            {
                ThingDef def = ctx.order[i];
                int remaining = ctx.need[def];

                List<Thing> list;
                if (!ctx.stacks.TryGetValue(def, out list)) return Fail(out things, out counts);

                for (int j = 0; j < list.Count && remaining > 0; j++)
                {
                    Thing t = list[j];
                    if (t == null || t.Destroyed) continue;

                    object owner = CraftIngredientClaims.OwnerOf(t);
                    if (owner != null && !ReferenceEquals(owner, ctx.line)) continue;

                    int take = Math.Min(remaining, t.stackCount);
                    if (take <= 0) continue;
                    if (!CraftIngredientClaims.TryClaim(t, ctx.line)) continue;

                    picked.Add(t);
                    nums.Add(take);
                    remaining -= take;
                }

                if (remaining > 0) return Fail(out things, out counts);
            }

            things = picked.ToArray();
            counts = nums.ToArray();
            return things.Length > 0;
        }

        private static bool Fail(out Thing[] things, out int[] counts)
        {
            things = null;
            counts = null;
            return false;
        }

        private static string Describe(Ctx ctx)
        {
            if (ctx.trail.Count == 0) return null;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < ctx.trail.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(ctx.trail[i]);
            }
            return sb.ToString();
        }
    }
}
