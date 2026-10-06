// =====================================================================================
//  【本地改动】Digital Storage 本地整合版 —— 本文件为**新增文件**（上游 main 分支没有）。
// -------------------------------------------------------------------------------------
//  本文件是什么：
//    AE2 式「合成树」的核心算法：从最终产物反查可用配方 → 递归展开中间产物 →
//    按 products[i].count 向上取整算"要做几次" → 扣减核心里已有量 → 检测循环依赖。
//
//  关键口径（都用原版 API，不自己发明）：
//    · 反查配方：在 comp.UnlockedRecipes（= 本代理范围内工作台能做、且已解锁的配方）里找
//      products 含该产物的那些。同一个产物可能有多条 ⇒ 玩家选过的优先，其次"原料种类少"，
//      最后按 defName 稳定排序（保证结果可复现）。
//    · 每次用量：IngredientCount.CountFor(RecipeDef)（原版自己的口径，含材质系数）
//    · 一次产出：RecipeDef.products[i].count（要除，向上取整）
//    · 核心现有：与制作面板同源（CoreFinder.AllUsableCores + GetDirectlyHeldThings）
//
//  哪些东西当"叶子"（不展开）：
//    · 可自选材质的槽位（IngredientCount 不是 IsFixedIngredient）—— 材质由玩家在订单上选
//    · 没有可用配方的产物（NoRecipe）
//    · 屠宰 / 熔炼这类 specialProducts —— 没法"做"出一头牛，只能当原料
//    · 循环依赖（CycleCut）与超深（depth >= MaxDepth）
//
//  怎么回退：删掉本文件 + 对应那一组改动（source-changes-groupI.ps1）即可。
// =====================================================================================
using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>合成树里的一个"原生材料"行（叶子）。</summary>
    internal class CraftTreeLeaf
    {
        /// <summary>具体原料（固定原料时有值；可自选材质的槽位为 null）。</summary>
        public ThingDef Def;

        /// <summary>显示名（可自选材质时是"木材/钢铁/…"这种摘要）。</summary>
        public string Label;

        /// <summary>合计需要多少。</summary>
        public int Count;

        /// <summary>核心里已有多少。</summary>
        public int InCore;

        /// <summary>true = 这是"可自选材质"的槽位（材料由订单上的材料设置决定）。</summary>
        public bool IsStuffSlot;
    }

    /// <summary>合成树的一个节点。</summary>
    internal class CraftTreeNode
    {
        public ThingDef Product;

        /// <summary>用哪条配方做它（null = 叶子：没有可用配方 / 循环 / 超深）。</summary>
        public RecipeDef Recipe;

        /// <summary>同一产物的其它可用配方（预览界面里可以点着换）。</summary>
        public List<RecipeDef> Alternatives = new List<RecipeDef>();

        /// <summary>需要产出多少个（**已扣掉核心里已有量**）—— 预览里"现在要做几次"看它。</summary>
        public int Required;

        /// <summary>
        /// 这条分支的**完整需要量**（没扣核心现有量）。
        /// 中间产物转成「维持数量」订单时用的是**这个数**：维持模式要的是"核心里保持 N 个"，
        /// 所以目标必须是完整需要量，而不是扣完现有量的差值 —— 否则会少维持（差多少少多少）。
        /// </summary>
        public int RequiredTotal;

        /// <summary>一次合成产出几个。</summary>
        public int YieldPerCraft = 1;

        /// <summary>要做几次（= ceil(Required / YieldPerCraft)）。</summary>
        public int Crafts;

        /// <summary>这件产物在核心里本来有多少（显示用，已扣除被更上层分支先用掉的份额）。</summary>
        public int InCore;

        /// <summary>核心里已经够了 ⇒ 这条分支不用合成（也不会往下展开）。</summary>
        public bool CoveredByCore;

        /// <summary>没有可用配方 ⇒ 只能手动准备。</summary>
        public bool NoRecipe;

        /// <summary>因循环依赖被截断。</summary>
        public bool CycleCut;

        /// <summary>配方含概率产出 ⇒ 数量按期望值算，实际可能多做或少做。</summary>
        public bool HasChanceProduct;

        public int Depth;

        public List<CraftTreeNode> Children = new List<CraftTreeNode>();

        /// <summary>本节点这一层直接消耗的原生材料（可自选材质的槽位）。</summary>
        public List<CraftTreeLeaf> Leaves = new List<CraftTreeLeaf>();
    }

    /// <summary>"要加进订单"的一行：某条配方 + 目标数量。</summary>
    internal class CraftTreePlanRow
    {
        public ThingDef Product;
        public RecipeDef Recipe;
        public int Total;
    }

    internal static class CraftTree
    {
        /// <summary>递归深度上限（防病态配方把界面/递归撑爆）。</summary>
        private const int MaxDepth = 12;

        /// <summary>玩家在同一产物上手动选过的配方（会话内记住；不写进存档）。</summary>
        private static readonly Dictionary<ThingDef, RecipeDef> chosen = new Dictionary<ThingDef, RecipeDef>();

        public static void ForgetChoices()
        {
            chosen.Clear();
        }

        public static void RememberChoice(ThingDef product, RecipeDef recipe)
        {
            if (product == null || recipe == null) return;
            chosen[product] = recipe;
        }

        /// <summary>
        /// 核心里每个 def 的现有量。与制作面板（Window_CraftAutomation.RebuildStock）**同源**，
        /// 免得两处口径漂移。
        /// </summary>
        public static Dictionary<ThingDef, int> CoreStock(Map map)
        {
            Dictionary<ThingDef, int> d = new Dictionary<ThingDef, int>();
            if (map == null) return d;
            List<Building_StorageCore> cores = CoreFinder.AllUsableCores(map);
            if (cores == null) return d;
            for (int i = 0; i < cores.Count; i++)
            {
                Building_StorageCore core = cores[i];
                if (core == null) continue;
                ThingOwner held = core.GetDirectlyHeldThings();
                if (held == null) continue;
                for (int k = 0; k < held.Count; k++)
                {
                    Thing t = held[k];
                    if (t == null || t.Destroyed || t.def == null) continue;
                    int cur;
                    d.TryGetValue(t.def, out cur);
                    d[t.def] = cur + Math.Max(0, t.stackCount);
                }
            }
            return d;
        }

        private static bool Produces(RecipeDef r, ThingDef product)
        {
            if (r == null || product == null || r.products == null) return false;
            for (int i = 0; i < r.products.Count; i++)
            {
                ThingDefCountClass p = r.products[i];
                if (p != null && p.thingDef == product) return true;
            }
            return false;
        }

                /// <summary>
        /// ★ 修"合成树展不开上游"：原版实现只认 DS 自己的可解锁配方表，
        /// 我们自定义的配方（挂在制作代理 WorkerCraft I~IV 上）不在里面 ⇒ 上游步骤查不到、树建不起来。
        /// 这里在结果里**并进"所有挂在制作代理上的、产物=这件物品"的配方**。
        /// </summary>
        public static List<RecipeDef> RecipesFor(CompBillAutomation comp, ThingDef product)
        {
            List<RecipeDef> list = RecipesForBase(comp, product);
            if (list == null) list = new List<RecipeDef>();
            if (product == null) return list;
            foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefs)
            {
                if (r == null || r.ProducedThingDef != product || r.recipeUsers == null) continue;
                bool onProxy = false;
                for (int u = 0; u < r.recipeUsers.Count; u++)
                {
                    ThingDef uu = r.recipeUsers[u];
                    if (uu != null && uu.defName != null && uu.defName.StartsWith("DigitalStorage_WorkerCraft")) { onProxy = true; break; }
                }
                if (onProxy && !list.Contains(r)) list.Add(r);
            }
            return list;
        }
/// <summary>
        /// 能产出该产物的可用配方（= 本代理范围内工作台能做、且已解锁的）。
        /// 顺序：玩家选过的 → 原料种类少的 → defName（稳定，保证同一存档里结果可复现）。
        /// </summary>
        private static List<RecipeDef> RecipesForBase(CompBillAutomation comp, ThingDef product)
        {
            List<RecipeDef> list = new List<RecipeDef>();
            if (comp == null || product == null) return list;
            IList<RecipeDef> all = comp.UnlockedRecipes;
            if (all == null) return list;
            for (int i = 0; i < all.Count; i++)
            {
                RecipeDef r = all[i];
                if (!Produces(r, product)) continue;
                list.Add(r);
            }
            list.Sort(delegate (RecipeDef a, RecipeDef b)
            {
                int na = (a.ingredients == null) ? 0 : a.ingredients.Count;
                int nb = (b.ingredients == null) ? 0 : b.ingredients.Count;
                if (na != nb) return na - nb;
                return string.CompareOrdinal(a.defName, b.defName);
            });
            RecipeDef pick;
            if (chosen.TryGetValue(product, out pick) && pick != null)
            {
                int idx = list.IndexOf(pick);
                if (idx > 0)
                {
                    list.RemoveAt(idx);
                    list.Insert(0, pick);
                }
            }
            return list;
        }

        /// <summary>
        /// 这条配方有没有"可代工"的中间产物 —— 用来决定点配方时要不要先弹合成树预览。
        /// 只看**固定原料**（可自选材质的槽位不展开）。
        /// </summary>
        public static bool HasCraftableIntermediate(CompBillAutomation comp, RecipeDef recipe)
        {
            if (comp == null || recipe == null || recipe.ingredients == null) return false;
            for (int i = 0; i < recipe.ingredients.Count; i++)
            {
                IngredientCount ing = recipe.ingredients[i];
                if (ing == null || !ing.IsFixedIngredient || ing.FixedIngredient == null) continue;
                if (RecipesFor(comp, ing.FixedIngredient).Count > 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 构建合成树。
        /// <para>wanted：最终产物要几个。中间产物的数量按每件用量换算，并**扣掉核心里已有量**。</para>
        /// </summary>
        public static CraftTreeNode Build(CompBillAutomation comp, RecipeDef rootRecipe, ThingDef rootProduct, int wanted)
        {
            CraftTreeNode root = new CraftTreeNode();
            root.Product = rootProduct;
            root.Recipe = rootRecipe;
            root.Depth = 0;
            if (comp == null || rootProduct == null || wanted <= 0) return root;

            Map map = (comp.parent != null) ? comp.parent.Map : null;
            Dictionary<ThingDef, int> avail = CoreStock(map);

            root.Required = wanted;
            root.RequiredTotal = wanted;
            int yield = 1;
            if (rootRecipe != null && rootRecipe.products != null)
            {
                int sum = 0;
                for (int i = 0; i < rootRecipe.products.Count; i++)
                {
                    ThingDefCountClass p = rootRecipe.products[i];
                    if (p == null || p.thingDef != rootProduct) continue;
                    sum += Math.Max(1, p.count);
                    if (p.IsChanceBased) root.HasChanceProduct = true;
                }
                if (sum > 0) yield = sum;
            }
            root.YieldPerCraft = yield;
            root.Crafts = (wanted + yield - 1) / yield;

            List<ThingDef> path = new List<ThingDef>();
            path.Add(rootProduct);
            if (rootRecipe != null && rootRecipe.ingredients != null)
            {
                for (int i = 0; i < rootRecipe.ingredients.Count; i++)
                {
                    IngredientCount ing = rootRecipe.ingredients[i];
                    if (ing == null) continue;
                    int need = Mathf.CeilToInt(ing.CountFor(rootRecipe)) * root.Crafts;
                    if (need <= 0) continue;
                    if (ing.IsFixedIngredient && ing.FixedIngredient != null)
                    {
                        root.Children.Add(BuildNode(comp, ing.FixedIngredient, need, path, avail, 1));
                    }
                    else
                    {
                        root.Leaves.Add(MakeStuffLeaf(ing, rootRecipe, need, avail));
                    }
                }
            }
            return root;
        }

        private static CraftTreeLeaf MakeStuffLeaf(IngredientCount ing, RecipeDef recipe, int need, Dictionary<ThingDef, int> avail)
        {
            CraftTreeLeaf leaf = new CraftTreeLeaf();
            leaf.IsStuffSlot = true;
            leaf.Label = ing.SummaryFor(recipe);
            leaf.Count = need;
            return leaf;
        }

        private static CraftTreeNode BuildNode(CompBillAutomation comp, ThingDef product, int wanted,
            List<ThingDef> path, Dictionary<ThingDef, int> avail, int depth)
        {
            CraftTreeNode node = new CraftTreeNode();
            node.Product = product;
            node.Depth = depth;
            if (product == null || wanted <= 0) return node;

            // ① 先扣"核心里已有"（AE2 也是这么做的：已有的不用再做）
            int have = 0;
            avail.TryGetValue(product, out have);
            int fromCore = Math.Min(have, wanted);
            node.InCore = fromCore;
            avail[product] = have - fromCore;
            int remaining = wanted - fromCore;
            node.RequiredTotal = wanted;
            node.Required = remaining;
            if (remaining <= 0)
            {
                node.CoveredByCore = true;
                return node;                      // 核心够 ⇒ 不展开子节点
            }

            // ② 循环依赖 / 深度上限
            for (int i = 0; i < path.Count; i++)
            {
                if (path[i] == product)
                {
                    node.CycleCut = true;
                    return node;
                }
            }
            if (depth >= MaxDepth)
            {
                node.NoRecipe = true;
                return node;
            }

            // ③ 反查配方
            List<RecipeDef> opts = RecipesFor(comp, product);
            if (opts.Count == 0)
            {
                node.NoRecipe = true;             // 只能手动准备（或从别处搞来）
                return node;
            }
            RecipeDef r = opts[0];
            node.Recipe = r;
            for (int i = 1; i < opts.Count; i++) node.Alternatives.Add(opts[i]);

            int yield = 0;
            if (r.products != null)
            {
                for (int i = 0; i < r.products.Count; i++)
                {
                    ThingDefCountClass p = r.products[i];
                    if (p == null || p.thingDef != product) continue;
                    yield += Math.Max(1, p.count);
                    if (p.IsChanceBased) node.HasChanceProduct = true;
                }
            }
            if (yield <= 0) yield = 1;
            node.YieldPerCraft = yield;
            node.Crafts = (remaining + yield - 1) / yield;

            // ④ 展开原料
            path.Add(product);
            if (r.ingredients != null)
            {
                for (int i = 0; i < r.ingredients.Count; i++)
                {
                    IngredientCount ing = r.ingredients[i];
                    if (ing == null) continue;
                    int need = Mathf.CeilToInt(ing.CountFor(r)) * node.Crafts;
                    if (need <= 0) continue;
                    if (ing.IsFixedIngredient && ing.FixedIngredient != null)
                    {
                        node.Children.Add(BuildNode(comp, ing.FixedIngredient, need, path, avail, depth + 1));
                    }
                    else
                    {
                        node.Leaves.Add(MakeStuffLeaf(ing, r, need, avail));
                    }
                }
            }
            path.RemoveAt(path.Count - 1);
            return node;
        }

        /// <summary>
        /// 汇总"要加进订单的中间产物"：把树上所有需要合成的节点按**产物聚合**（同一产物可能
        /// 出现在多条分支上），得到 产物 → 合计需要多少个。根节点本身不算在内（它由调用方处理）。
        /// <para>被核心现有量覆盖、没有配方、循环截断的节点都不算。</para>
        /// </summary>
        public static List<CraftTreePlanRow> FlattenIntermediate(CraftTreeNode root)
        {
            List<CraftTreePlanRow> rows = new List<CraftTreePlanRow>();
            if (root == null) return rows;
            Dictionary<ThingDef, CraftTreePlanRow> agg = new Dictionary<ThingDef, CraftTreePlanRow>();
            Collect(root, agg);
            foreach (KeyValuePair<ThingDef, CraftTreePlanRow> kv in agg) rows.Add(kv.Value);
            rows.Sort(delegate (CraftTreePlanRow a, CraftTreePlanRow b)
            {
                return string.CompareOrdinal(a.Recipe.defName, b.Recipe.defName);
            });
            return rows;
        }

        private static void Collect(CraftTreeNode node, Dictionary<ThingDef, CraftTreePlanRow> agg)
        {
            if (node == null) return;
            // 只把"真正需要合成"的中间产物算进订单：有配方、需要量 > 0、没被核心现有量覆盖、且不是根节点
            if (node.Recipe != null && node.RequiredTotal > 0 && !node.CoveredByCore && node.Depth > 0)
            {
                CraftTreePlanRow row;
                if (!agg.TryGetValue(node.Product, out row))
                {
                    row = new CraftTreePlanRow();
                    row.Product = node.Product;
                    row.Recipe = node.Recipe;
                    row.Total = 0;
                    agg[node.Product] = row;
                }
                row.Total += node.RequiredTotal;
            }
            for (int i = 0; i < node.Children.Count; i++) Collect(node.Children[i], agg);
        }

        /// <summary>把整棵树的原生材料汇总成 表（固定原料 + 可自选材质槽位），供预览界面显示。</summary>
        public static List<CraftTreeLeaf> CollectRawMaterials(CraftTreeNode root, Dictionary<ThingDef, int> coreStock)
        {
            List<CraftTreeLeaf> list = new List<CraftTreeLeaf>();
            Dictionary<ThingDef, CraftTreeLeaf> agg = new Dictionary<ThingDef, CraftTreeLeaf>();
            Dictionary<string, CraftTreeLeaf> stuffAgg = new Dictionary<string, CraftTreeLeaf>();
            CollectRaw(root, agg, stuffAgg);
            foreach (KeyValuePair<ThingDef, CraftTreeLeaf> kv in agg)
            {
                if (coreStock != null)
                {
                    int have;
                    if (coreStock.TryGetValue(kv.Key, out have)) kv.Value.InCore = have;
                }
                list.Add(kv.Value);
            }
            foreach (KeyValuePair<string, CraftTreeLeaf> kv in stuffAgg) list.Add(kv.Value);
            list.Sort(delegate (CraftTreeLeaf a, CraftTreeLeaf b)
            {
                return string.CompareOrdinal(a.Label, b.Label);
            });
            return list;
        }

        private static void CollectRaw(CraftTreeNode node, Dictionary<ThingDef, CraftTreeLeaf> agg,
            Dictionary<string, CraftTreeLeaf> stuffAgg)
        {
            if (node == null) return;

            // 叶子原料：没有配方 / 循环截断 / 超深 ⇒ 只能靠手动准备
            bool isLeaf = (node.Recipe == null) && node.RequiredTotal > 0 && !node.CoveredByCore;
            if (isLeaf && node.Product != null)
            {
                CraftTreeLeaf leaf;
                if (!agg.TryGetValue(node.Product, out leaf))
                {
                    leaf = new CraftTreeLeaf();
                    leaf.Def = node.Product;
                    leaf.Label = node.Product.LabelCap;
                    leaf.Count = 0;
                    agg[node.Product] = leaf;
                }
                leaf.Count += node.RequiredTotal;
            }

            // 可自选材质的槽位（按摘要文案聚合，因为它的具体材料由订单上的设置决定）
            for (int i = 0; i < node.Leaves.Count; i++)
            {
                CraftTreeLeaf src = node.Leaves[i];
                if (src == null) continue;
                CraftTreeLeaf leaf;
                if (!stuffAgg.TryGetValue(src.Label, out leaf))
                {
                    leaf = new CraftTreeLeaf();
                    leaf.IsStuffSlot = true;
                    leaf.Label = src.Label;
                    leaf.Count = 0;
                    stuffAgg[src.Label] = leaf;
                }
                leaf.Count += src.Count;
            }

            for (int i = 0; i < node.Children.Count; i++) CollectRaw(node.Children[i], agg, stuffAgg);
        }

        /// <summary>树里是否有"需要注意"的地方（缺配方 / 循环 / 概率产出 / 多配方），预览界面用来决定要不要显示警告行。</summary>
        public static void CountWarnings(CraftTreeNode root, out int noRecipe, out int cycle, out int chance, out int ambiguous)
        {
            noRecipe = 0; cycle = 0; chance = 0; ambiguous = 0;
            CountWarningsNode(root, ref noRecipe, ref cycle, ref chance, ref ambiguous);
        }

        private static void CountWarningsNode(CraftTreeNode node, ref int noRecipe, ref int cycle, ref int chance, ref int ambiguous)
        {
            if (node == null) return;
            if (node.NoRecipe && node.Required > 0) noRecipe++;
            if (node.CycleCut) cycle++;
            if (node.HasChanceProduct) chance++;
            if (node.Alternatives.Count > 0) ambiguous++;
            for (int i = 0; i < node.Children.Count; i++)
            {
                CountWarningsNode(node.Children[i], ref noRecipe, ref cycle, ref chance, ref ambiguous);
            }
        }

        // =================================================================================
        // ★ 第 ② 批新增：「连中间产物一起加」的唯一实现
        // =================================================================================

        /// <summary>这条配方一次产出几个该产物（格子角标要用；找不到就返回 1）。</summary>
        public static int YieldOf(RecipeDef r, ThingDef product)
        {
            if (r == null || product == null || r.products == null) return 1;
            int sum = 0;
            for (int i = 0; i < r.products.Count; i++)
            {
                ThingDefCountClass p = r.products[i];
                if (p != null && p.thingDef == product) sum += Math.Max(1, p.count);
            }
            return (sum > 0) ? sum : 1;
        }

        /// <summary>在代理的订单里找这条配方的 plan（AddPlan 不返回它，只能这样找）。</summary>
        internal static CraftPlan FindPlan(CompBillAutomation comp, RecipeDef recipe)
        {
            if (comp == null || recipe == null) return null;
            IList<CraftPlan> all = comp.PlansForReading;
            if (all == null) return null;
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] != null && all[i].recipe == recipe) return all[i];
            }
            return null;
        }

        /// <summary>
        /// 把「最终产物 + 整条中间产物链」加成**维持模式**订单。
        ///
        /// <para><b>已有订单不覆盖</b>：<c>comp.AddPlan</c> 遇到已存在的配方会直接返回（不新建），
        /// 所以这里先查有没有，只有**新建的**才设目标 —— 否则会把玩家原来的"无限/固定次数"
        /// 悄悄改成维持模式。</para>
        ///
        /// <para>目标用的是 <c>RequiredTotal</c>（完整需要量），不是扣完库存的差值：维持模式要的是
        /// "核心里保持 N 个"。</para>
        ///
        /// <para>这是"连中间产物一起加"的**唯一实现**：合成树窗口与面板内联添加都调它。</para>
        /// </summary>
        public static void ApplyToPlans(CompBillAutomation comp, RecipeDef recipe, ThingDef product,
            int wanted, out int added, out int skipped)
        {
            added = 0;
            skipped = 0;
            if (comp == null || recipe == null) return;
            int want = Mathf.Max(1, wanted);

            // ★ AI 组：这里从"逐条加 plan"改成"**提交一个合成 Job**"（AE2 的一个请求 = 一棵依赖树）。
            //   对外语义完全不变（合成树窗口与面板内联添加都照旧调它、消息里的数字照样报），
            //   改的是**结构**：整条链现在属于同一个 Job ⇒ 取消时整树一起消失（用户报的那个 bug）。
            //   真正的建树/建单逻辑搬到了 CompBillAutomation.SubmitJob。
            int before = (comp.PlansForReading == null) ? 0 : comp.PlansForReading.Count;
            CraftJob job = comp.SubmitJob(recipe, product, want, true);
            if (job == null) { skipped++; return; }
            int after = (comp.PlansForReading == null) ? 0 : comp.PlansForReading.Count;

            added = Mathf.Max(0, after - before);              // 这次新建了几条订单
            skipped = Mathf.Max(0, job.steps.Count - added);   // 已有订单（没被覆盖）的步骤数
        }
    }
}
