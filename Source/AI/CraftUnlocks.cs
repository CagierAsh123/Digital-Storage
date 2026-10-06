using System.Collections.Generic;
using RimWorld;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// <b>"哪些配方能做"的索引</b> —— 面板菜单里的可选项就来自这里。
    ///
    /// <para>用户拍板："有该工作台才解锁该 bill" ⇒ 解锁判据 = 13×13 范围内**存在**能承载该配方的建筑类型
    /// （<c>ThingDef.recipes</c>，即该建筑能做的配方表）。**工作台上不需要真的有一条 bill**，
    /// 也不管它当前是否通电 —— 解锁是"你拥有这种机器"，生产线才要求"这台机器现在能用"。</para>
    ///
    /// <para>按 <c>ThingDef</c> 缓存：一个存档里台子类型就那么几种，缓存后每次扫描只是并集。</para>
    /// </summary>
    internal static class CraftUnlocks
    {
        private static readonly Dictionary<ThingDef, List<RecipeDef>> cache =
            new Dictionary<ThingDef, List<RecipeDef>>();

        /// <summary>某个工作台 def 能做的全部配方（已过滤掉不该出现的）。</summary>
        public static List<RecipeDef> For(ThingDef benchDef)
        {
            List<RecipeDef> list;
            if (cache.TryGetValue(benchDef, out list)) return list;

            list = new List<RecipeDef>();
            // ⚠️ 必须用 AllRecipes 而不是 def.recipes：XML 里 <recipes> 通常只写显式的几个，
            //    工作台的配方绝大多数来自"产品 def 上的 <recipeMaker> + recipeUsers"这一路，
            //    只有 AllRecipes 会把两路合并（ThingDef.cs:545-570 的第二趟扫描就是干这个的）。
            //    ⚠️ AllRecipes 返回的是它自己的缓存表，**只能读，不能改**。
            List<RecipeDef> defs = benchDef.AllRecipes;
            if (defs != null)
            {
                for (int i = 0; i < defs.Count; i++)
                {
                    RecipeDef r = defs[i];
                    if (IsAutomationCandidate(r)) list.Add(r);
                }
            }
            cache[benchDef] = list;
            return list;
        }

        /// <summary>
        /// 能让自动化做的配方要满足：
        /// <list type="number">
        /// <item><b>真的产出东西</b> —— 有 <c>products</c> 或有 <c>specialProducts</c>（熔炼/屠宰/切石）。
        ///   手术、植入体这类 <c>products</c> 为空的配方在建筑上本来也不会出现，这里再挡一道。</item>
        /// <item><b>未完成品（UFT）配方：现在能做</b> —— <c>UsesUnfinishedThing</c>（衣物、雕塑、艺术，
        ///   以及 mod 的同类配方）。做法见 <see cref="BillProbe"/> 的 <c>WorkAmountFor</c>：原版把 UFT
        ///   当作"跨 job 的进度载体"，而制作代理的进度本来就存在 <c>CraftLine</c> 上、一轮做完
        ///   ⇒ <b>代理不造 UFT，直接出成品</b>（不会在地上留半成品、也不会出现"幽灵作者"的报废件）。</item>
        /// <item><b>不是手术</b> —— 手术配方的 <c>recipeUsers</c> 是人不是建筑，正常进不来，
        ///   但 mod 可能塞进来，挡一道便宜。</item>
        /// </list>
        /// 注意：**研究门槛（<c>RecipeDef.AvailableNow</c>）不在这里过滤** —— 它随研究进度变化，
        /// 而本方法按 def 缓存。研究门槛在 <see cref="MenuRecipes"/> 每次扫描时过滤。
        /// </summary>
        private static bool IsAutomationCandidate(RecipeDef r)
        {
            if (r == null) return false;
            // ⚠️ 这里**故意不再**挡 r.UsesUnfinishedThing（旧版是 `if (r.UsesUnfinishedThing) return false;`）。
            //    UFT 配方改由 BillProbe 按"一步做到成品"的方式支持；不要把它加回来。
            if (r.IsSurgery) return false;
            bool produces = (r.products != null && r.products.Count > 0)
                || (r.specialProducts != null && r.specialProducts.Count > 0);
            return produces;
        }

        /// <summary>
        /// 面板菜单的最终列表：范围内所有台子类型的配方并集，再逐个过滤研究门槛，按名字排序。
        /// <paramref name="benchDefs"/> 是本次扫描到的台子 def 集合。
        /// </summary>
        public static void MenuRecipes(HashSet<ThingDef> benchDefs, List<RecipeDef> outList)
        {
            outList.Clear();
            if (benchDefs == null || benchDefs.Count == 0) return;

            HashSet<RecipeDef> seen = tmpSeen;
            seen.Clear();
            foreach (ThingDef def in benchDefs)
            {
                List<RecipeDef> rs = For(def);
                for (int i = 0; i < rs.Count; i++)
                {
                    RecipeDef r = rs[i];
                    if (seen.Contains(r)) continue;
                    if (!r.AvailableNow) continue;      // 研究 / meme / ideo 门槛
                    seen.Add(r);
                    outList.Add(r);
                }
            }
            seen.Clear();
            outList.Sort((a, b) => string.Compare(a.LabelCap, b.LabelCap, System.StringComparison.Ordinal));
        }

        private static readonly HashSet<RecipeDef> tmpSeen = new HashSet<RecipeDef>();
    }
}
