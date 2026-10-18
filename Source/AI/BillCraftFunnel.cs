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
            StoreProducts(comp, plan, products, map, bench);
            // ★ "造完就立马扫出来"：产物入库后立刻按新数量重算维持状态，
            //   否则调度会在计数更新前又配新线 ⇒ 同一步骤多做 1 个。
            //
            //   ★★ 注意：这里要重算的是**这一件产物对应的那条订单**，不是根订单！
            //   上游步骤（中间产物）的维持订单和根是两条不同的 plan，原来只重算 plan（=根）
            //   ⇒ 中间产物那条要等 30 tick 才更新 ⇒ 中间产物多做（实机反馈：
            //   "一直在造一个东西然后读不到数量增加"）。
            if (bill != null && bill.recipe != null)
            {
                CraftPlan ownerPlan = CraftTree.FindPlan(comp, bill.recipe);
                if (ownerPlan != null) comp.RecountNow(ownerPlan);
            }
            comp.RecountNow(plan);
            Performance.DevDrawProfiler.Bump("账单完成", 1);
            return CraftResult.Done;
        }

        /// <summary>
        /// 产物去向（用户拍板）：**直塞最近的核心**；核心不收（过滤器/容量）或没有核心 ⇒ 落在台子旁。
        /// 不走 <c>bill.GetStoreMode()</c> —— 自动化出来的东西一律进核心，"落地"只是失败兜底。
        /// </summary>
        private static void StoreProducts(CompBillAutomation comp, CraftPlan plan, List<Thing> products, Map map, Thing bench)
        {
            if (products == null || products.Count == 0) return;

            Building_StorageCore core = DigitalDropRedirect.NearestUsableCore(map, bench.PositionHeld);
            IntVec3 cell = bench.PositionHeld;

            // ★ 兜底：NearestUsableCore 只认【已通电】的核心（IsUsableNow = Spawned && Powered）。
            //   核心没电 / 刚好在断电瞬间时它会返回 null ⇒ 产物全部落地 ⇒
            //   「制作完成：X（核心放不下或未启用，放在工作台旁）」一直刷。
            //   这里退一步：本图有核心就挑最近的那台（哪怕没电）——产物进核心比落一地好，
            //   等来电了自己会被正常使用。
            if (core == null)
            {
                core = FindNearestCoreIgnoringPower(map, bench.PositionHeld);
                if (core != null)
                {
                    Log.ErrorOnce("[DigitalStorage] 没有【已通电】的核心，产物改送最近的核心（"
                        + core.Position + "）—— 检查核心供电。", 0x44534398);
                }
            }

            for (int i = 0; i < products.Count; i++)
            {
                Thing p = products[i];
                if (p == null || p.Destroyed) continue;

                // ★ 恢复原语义：Accepts 说不收就**落地**（不再绕过核心自己的存储筛选/容量判断直塞容器）。
                //   "绕过"会把被禁用的物品塞进核心、并且每件都打一条未限流的 Warning（刷日志），
                //   两件事都不该进上游。
                if (core != null && DigitalDropRedirect.TryIngestUnspawned(core, p))
                {
                    comp.NoteProduct(p, true);
                    continue;
                }

                // ★ 修复（用户反馈："造好的树测试的物品没被吸进核心里"）：
                //   口径：**自动合成出来的产物一律优先送进核心**，只受容量 + 入库开关约束。
                //   原来要求"这件产物是某个在跑 Job 需要的"才允许绕过筛选，但合成**做完那一刻**
                //   Job 可能已经结束 / 已被清理 ⇒ 判定 false ⇒ 产物落地
                //   （实况：已产出 26 件、落地 12 件）。
                //
                //   ⚠️ 适配上游 2026-10-12 的重构：旧接口
                //     DigitalDropRedirect.TryIngestUnspawnedIgnoringFilter
                //     Building_StorageCore.AcceptsByCapacityOnly
                //   上游已删除（新版 TryStore 一律走 Accepts ⇒ 受筛选约束）。
                //   这里自己判：容器有容量 + 入库开关/电力允许 ⇒ 直接塞进容器（跳过筛选）。
                if (core != null && TryIngestIgnoringFilter(core, p))
                {
                    comp.NoteProduct(p, true);
                    continue;
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
        /// 本图**最近的核心**，<b>不管有没有电</b>（供 <see cref="StoreProducts"/> 兜底用）。
        /// 只在"没有已通电核心"时调用，所以不做缓存 —— 顺手遍历一次就够。
        /// </summary>
        private static Building_StorageCore FindNearestCoreIgnoringPower(Map map, IntVec3 near)
        {
            if (map == null) return null;
            List<Thing> all = map.listerThings.AllThings;
            if (all == null) return null;

            Building_StorageCore best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < all.Count; i++)
            {
                Building_StorageCore c = all[i] as Building_StorageCore;
                if (c == null || !c.Spawned || c.Destroyed) continue;
                float d = (c.Position - near).LengthHorizontalSquared;
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best;
        }

        /// <summary>
        /// 把**未落地**的产物优先送进核心（自动合成产物专用）。
        ///
        /// <para><b>为什么需要这个入口</b>：上游 2026-10-12 把 <c>innerContainer</c> 从
        /// <c>ThingOwner&lt;Thing&gt;</c> 换成了 <c>CoreStorageContainer</c>，而它的 <c>TryAdd</c>
        /// 会先问 <c>OwnerAllowsInsert</c>（入库开关 + 电力 + <b>存储筛选</b>）——
        /// 实测数据：<c>take=1 canAccept=1 added=0 FilterAllows=False</c>，
        /// 也就是说**只要筛选里没这个 def，从容器外面怎么写都塞不进去**。
        /// 而自动合成的产物不能因为没勾筛选器就落地 —— 那会让前置树的下一步永远等不到料
        /// （链断在第二步），实机现象是「制作完成：X（核心放不下或未启用，放在工作台旁）」一直刷。</para>
        ///
        /// <para>真正的处理在 <c>Building_StorageCore.TryStoreIgnoringFilter</c>：
        /// 它**先把该 def 允许进筛选**，再走正常 <c>TryStore</c>（失败了还会撤回去），
        /// 这样原版管线看到的仍是"核心确实收这个 def"。</para>
        ///
        /// <para>口径仍然窄：**只有自动合成的产物**走这条；玩家手工搬运、搬运 mod
        /// 一律照旧走 <c>Accepts</c>（受筛选约束），那条路不经过这里。</para>
        /// </summary>
        private static bool TryIngestIgnoringFilter(Building_StorageCore core, Thing t)
        {
            if (core == null || t == null || t.Destroyed) return false;
            if (t.Spawned || t.holdingOwner != null) return false;   // 只处理未落地的
            int added = core.TryStoreIgnoringFilter(t, t.stackCount);
            if (added <= 0)
            {
                // 进不去就写一条（限流）：省得下次排查靠猜
                Log.ErrorOnce("[DigitalStorage] 合成产物未进核心：" + t.def.defName
                    + " | Spawned=" + core.Spawned
                    + " 入库开关=" + core.HaulDestinationEnabled
                    + " Powered=" + core.Powered
                    + " 容量还能收=" + core.GetDirectlyHeldThings().GetCountCanAccept(t)
                    + " ⇒ 将落在台子旁", 0x44534399);
            }
            return added > 0;
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
