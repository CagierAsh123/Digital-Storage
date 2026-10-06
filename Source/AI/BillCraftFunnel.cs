using System;
using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Components;
using DigitalStorage.Core;
using RimWorld;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// <b>结算</b>：把槽位里那一轮做成的活落成产物 —— 照抄
    /// <c>Toils_Recipe.FinishRecipeAndStartStoringProduct</c>（<c>Verse.AI\Toils_Recipe.cs:171-300</c>）的顺序，
    /// 只是"原料已经在容器里"所以省掉搬运那一段。
    ///
    /// <para><b>三个唯一漏斗，一个都不自己写</b>：</para>
    /// <list type="number">
    /// <item>产物 → <c>GenRecipe.MakeRecipeProducts</c>（品质/染色/特殊产物/压缩全在里面，<c>GenRecipe.cs:9-72</c>）；</item>
    /// <item>扣料 → <c>recipe.Worker.ConsumeIngredient</c>（<c>RecipeWorker.cs:44</c>，默认 = <c>Destroy()</c>，
    ///   而 <c>Thing.Destroy</c> 会 <c>holdingOwner.Notify_ContainedItemDestroyed</c>，<c>Thing.cs:1085</c>
    ///   ⇒ 原料自动离开容器，不会留僵尸条目）；</item>
    /// <item>bill 状态 → <c>bill.Notify_IterationCompleted</c>（<c>Bill_Production.cs:245-259</c>：减 <c>repeatCount</c>、
    ///   回调 <c>recipe.Worker</c>）。**绝不自己改 repeatCount**。</item>
    /// </list>
    ///
    /// <para><b>铁律：绝不吞料</b>。整堆取走的（<c>need &gt;= stackCount</c>）压根没离开容器，不用管；
    /// 拆堆取出的（<c>SplitOff</c>）如果后面生成产物失败，必须原样放回容器。</para>
    /// </summary>
    internal static class BillCraftFunnel
    {
        public static CraftResult TryComplete(CompBillAutomation comp, CraftPlan plan, CraftLine line, Map map, Pawn w)
        {
            if (plan == null || plan.recipe == null || !plan.Maintained) return CraftResult.Invalid;

            Bill_Production bill = line.Bill;
            if (bill == null || bill.DeletedOrDereferenced) return CraftResult.Invalid;

            // 作废闸门：配方还在不在（研究被撤销 / def 没了）/ 台子还能不能用。
            // 注意这里**没有** bill.ShouldDoNow()：那是原版按 bill 的 repeatCount 判"还要不要做"，
            // 而我们的次数记在 CraftPlan 上（临时的 bill 是 Forever，恒真）。plan.Active 就是同一件事。
            if (!plan.recipe.AvailableNow) return CraftResult.Invalid;

            Thing bench = line.Bench;
            if (bench == null || bench.Destroyed || !bench.Spawned) return CraftResult.Invalid;
            IBillGiver giver = bench as IBillGiver;
            if (giver == null) return CraftResult.Invalid;
            if (!giver.CurrentlyUsableForBills()) return CraftResult.Invalid;

            // 每 tick 完成预算（与其余代理工人共用同一笔"原版单件代价"）。
            // ⚠️ 这一条必须放在所有"作废判定"之后：拿不到票只是限流，活已经干完了，进度不能丢。
            if (!DigitalWorkBudget.AllowCompletion()) return CraftResult.NoBudget;

            // ① 切料：照抄 Toils_Recipe.CalculateIngredients:321 的规则（整堆够就整堆拿，否则 SplitOff）。
            //    同时记住来源容器，出错能原样放回。
            List<Thing> ingredients = new List<Thing>();
            List<ThingOwner> owners = new List<ThingOwner>();
            for (int i = 0; i < line.Ingredients.Length; i++)
            {
                Thing t = line.Ingredients[i];
                if (t == null || t.Destroyed) { Rollback(ingredients, owners); return CraftResult.Invalid; }

                Building_StorageCore core = t.ParentHolder as Building_StorageCore;
                if (core == null || core.Map != map) { Rollback(ingredients, owners); return CraftResult.Invalid; }

                int need = line.Counts[i];
                if (need <= 0) continue;

                if (need >= t.stackCount)
                {
                    ingredients.Add(t);
                    owners.Add(null);          // 整堆还在容器里，不需要回滚
                }
                else
                {
                    Thing part = t.SplitOff(need);
                    if (part == null) { Rollback(ingredients, owners); return CraftResult.Invalid; }
                    ingredients.Add(part);
                    owners.Add(core.GetDirectlyHeldThings());
                }
            }

            // ② 产物（唯一漏斗）。生成期间抑制"大师/传奇"信件 —— 工厂量产会把信件栏刷爆；
            //    品质信息由左上角那条完成提示带上（见 CompBillAutomation.NoteProduct / DescribeProduct）。
            Thing dominant = DominantIngredient(bill.recipe, ingredients, line.StuffDef);
            List<Thing> products;
            bool prevSuppress = BillAutomationScope.SuppressCraftLetters;
            BillAutomationScope.SuppressCraftLetters = true;
            try
            {
                products = GenRecipe.MakeRecipeProducts(bill.recipe, w, ingredients, dominant, giver,
                    bill.precept, bill.style, bill.graphicIndexOverride).ToList();
            }
            catch (Exception e)
            {
                Log.Error("[DigitalStorage] 制作代理生成产物失败（本次取消，料已放回）："
                    + bill.recipe.defName + " :: " + e);
                Rollback(ingredients, owners);
                return CraftResult.Invalid;
            }
            finally
            {
                BillAutomationScope.SuppressCraftLetters = prevSuppress;
            }

            // ③ 扣料（唯一漏斗）
            for (int i = 0; i < ingredients.Count; i++)
            {
                try
                {
                    bill.recipe.Worker.ConsumeIngredient(ingredients[i], bill.recipe, map);
                }
                catch (Exception e)
                {
                    Log.Error("[DigitalStorage] 制作代理扣料失败（这一件没扣掉）：" + ingredients[i] + " :: " + e);
                }
            }

            // ④ bill 状态 + 统计钩子。顺序照抄 Toils_Recipe.cs:133 → :200-201 → :221。
            //    我们的 bill 是 Forever ⇒ Notify_IterationCompleted 不会动次数（次数在 plan 上），
            //    但它会回调 recipe.Worker.Notify_IterationCompleted —— 那是 mod 的扩展点，必须走。
            try
            {
                bill.Notify_BillWorkFinished(w);
                bill.Notify_IterationCompleted(w, ingredients);
                RecordsUtility.Notify_BillDone(w, products);
                if (products.Count > 0)
                {
                    Find.QuestManager.Notify_ThingsProduced(w, products);
                }
            }
            catch (Exception e)
            {
                Log.Error("[DigitalStorage] 制作代理 bill 结算回调抛异常（产物已出，忽略）：" + e);
            }

            // ⑤ 记账（**唯一入口**：次数/统计都在 CraftPlan.NoteCompleted 里）
            plan.NoteCompleted();

            // ⑥ 产物：直塞核心，塞不进就落在台子旁（绝不吞）
            StoreProducts(comp, products, map, bench);
            Performance.DevDrawProfiler.Bump("账单完成", 1);
            return CraftResult.Done;
        }

        /// <summary>
        /// 产物去向（用户拍板）：**直塞最近的核心**；核心不收（过滤器/容量）或没有核心 ⇒ 落在台子旁。
        /// 不走 <c>bill.GetStoreMode()</c> —— 自动化出来的东西一律进核心，"落地"只是失败兜底。
        /// </summary>
        private static void StoreProducts(CompBillAutomation comp, List<Thing> products, Map map, Thing bench)
        {
            if (products == null || products.Count == 0) return;

            Building_StorageCore core = DigitalDropRedirect.NearestUsableCore(map, bench.PositionHeld);
            IntVec3 cell = bench.PositionHeld;

            for (int i = 0; i < products.Count; i++)
            {
                Thing p = products[i];
                if (p == null || p.Destroyed) continue;

                if (core != null)
                {
                    if (DigitalDropRedirect.TryIngestUnspawned(core, p))
                    {
                        comp.NoteProduct(p, true);
                        continue;
                    }
                    // 适配：核心明明全收却仍被判不收 ⇒ 直接往核心容器里塞，并写日志说明是哪一关挡的
                    try
                    {
                        StorageSettings __st = core.GetStoreSettings();
                        bool __filterOk = (__st == null || __st.filter == null) || __st.filter.Allows(p);
                        int __can = core.GetDirectlyHeldThings().GetCountCanAccept(p);
                        Log.Warning("[适配] 产物未过 Accepts：def=" + p.def.defName
                            + " 已生成=" + core.Spawned
                            + " 接收开关=" + core.HaulDestinationEnabled
                            + " 过滤放行=" + __filterOk
                            + " 现有堆数=" + core.GetDirectlyHeldThings().Count
                            + " 还能吃=" + __can);
                        if (core.GetDirectlyHeldThings().TryAdd(p, true))
                        {
                            comp.NoteProduct(p, true);
                            Log.Warning("[适配] 已绕过 Accepts 直接入库：" + p.def.defName);
                            continue;
                        }
                    }
                    catch (Exception __ex)
                    {
                        Log.Warning("[适配] 兜底入库异常：" + __ex.Message);
                    }
                }
                if (GenPlace.TryPlaceThing(p, cell, map, ThingPlaceMode.Near))
                {
                    comp.NoteProduct(p, false);
                    continue;
                }

                // 既塞不进也落不了地：报错 + 销毁。**留着才是真的丢**（未 spawn 且无归属的 Thing
                // 谁也看不见、也不会被存档，只是内存里的幽灵）。
                Log.Error("[DigitalStorage] 制作代理产物既塞不进核心也落不了地，已丢弃：" + p);
                if (!p.Destroyed) p.Destroy();
            }
        }

        /// <summary>
        /// 照抄原版 <c>Toils_Recipe.CalculateDominantIngredient</c>。
        /// 这个值决定 stuff 类产物的材质与颜色，**传 null 会在 GenRecipe.cs:21 直接 NRE**。
        ///
        /// <para><b>未完成品（UFT）那一支现在补回来了。</b>原版对该配方的第一支是
        /// <c>if (uft != null &amp;&amp; uft.def.MadeFromStuff) return uft.ingredients.First(ing =&gt; ing.def == uft.Stuff);</c>
        /// —— 即"认未完成品身上那个 stuff"。代理不造 UFT，但把同一件事记在
        /// <see cref="CraftLine.StuffDef"/> 上（取活时定，见 <see cref="BillProbe.UftStuffDef"/>），
        /// 于是这里用 <paramref name="preferredStuff"/> 复现同一支，保证"算工时用的材质"与"做产物用的材质"一致。</para>
        ///
        /// <para>普通配方的 <paramref name="preferredStuff"/> 恒为 null，行为和改之前完全一样。</para>
        /// </summary>
        internal static Thing DominantIngredient(RecipeDef recipe, IList<Thing> ingredients, ThingDef preferredStuff)
        {
            if (ingredients == null || ingredients.Count == 0) return null;

            // ★ UFT 配方：取活时定下的主材质优先（= 原版 uft.ingredients.First(ing => ing.def == uft.Stuff)）
            if (preferredStuff != null)
            {
                for (int i = 0; i < ingredients.Count; i++)
                {
                    Thing t = ingredients[i];
                    if (t != null && t.def == preferredStuff) return t;
                }
            }

            if (recipe.productHasIngredientStuff) return ingredients[0];

            bool stuffProduct = false;
            if (recipe.products != null)
            {
                for (int i = 0; i < recipe.products.Count; i++)
                {
                    ThingDefCountClass c = recipe.products[i];
                    if (c != null && c.thingDef != null && c.thingDef.MadeFromStuff) { stuffProduct = true; break; }
                }
            }
            if (recipe.unfinishedThingDef != null && recipe.unfinishedThingDef.MadeFromStuff) stuffProduct = true;

            if (stuffProduct)
            {
                List<Thing> stuffs = new List<Thing>();
                for (int i = 0; i < ingredients.Count; i++)
                {
                    Thing t = ingredients[i];
                    if (t != null && t.def != null && t.def.IsStuff) stuffs.Add(t);
                }
                if (stuffs.Count > 0) return stuffs.RandomElementByWeight(t => t.stackCount);
                // 原版这条路上会拿到 null（产物 MadeFromStuff 却没有 stuff 原料）⇒ 后面 NRE。
                // 退化成第一件，比崩掉好；真出现说明这个配方本身有问题。
                return ingredients[0];
            }
            return ingredients.RandomElementByWeight(t => t.stackCount);
        }

        /// <summary>把 <c>SplitOff</c> 出来但没用上的料放回原容器（放不回就落地，绝不销毁）。</summary>
        private static void Rollback(List<Thing> ingredients, List<ThingOwner> owners)
        {
            for (int i = 0; i < ingredients.Count; i++)
            {
                if (i >= owners.Count || owners[i] == null) continue;   // 整堆那种：没离开容器
                Thing t = ingredients[i];
                if (t == null || t.Destroyed) continue;

                ThingOwner owner = owners[i];
                if (owner.TryAdd(t, true)) continue;

                Thing holder = owner.Owner as Thing;
                Map map = (holder == null) ? null : holder.MapHeld;
                if (map != null && GenPlace.TryPlaceThing(t, holder.PositionHeld, map, ThingPlaceMode.Near)) continue;

                Log.Error("[DigitalStorage] 制作代理无法把原料放回容器，也没落成地：" + t);
            }
        }

        /// <summary>
        /// 给左上角提示用的产物名（带品质前缀，例如"传奇 钢铁长剑"）。
        /// 品质信被抑制了，信息在这一条里补回来。
        /// </summary>
        public static string DescribeProduct(Thing p)
        {
            if (p == null || p.def == null) return "?";
            string label;
            try { label = p.LabelNoCount; }
            catch { label = p.def.LabelCap; }

            QualityCategory q;
            if (p.TryGetQuality(out q)) label = QualityUtility.GetLabel(q) + " " + label;
            return label;
        }
    }
}
