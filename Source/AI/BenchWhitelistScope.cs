namespace DigitalStorage.AI
{
    /// <summary>
    /// 制作代理"取活"期间，是否放宽原版 <c>WorkGiver_DoBill.ThingIsUsableBillGiver</c> 的白名单。
    ///
    /// <para>社区反馈 2026-10-18 第 2 条（制作者：DF Express「制作代理丢工作间只识别切石砖、
    /// 缝杂皮和做 EMP 炮弹，其他配方都不显示」）的根因之一：面板能列出的配方来自
    /// <c>ThingDef.AllRecipes</c>，但取活要过原版的第一道门 ——
    /// <code>
    /// // WorkGiver_DoBill.cs:370
    /// if (def.fixedBillGiverDefs != null &amp;&amp; def.fixedBillGiverDefs.Contains(thing.def)) return true;
    /// </code>
    /// 原版 19 个 DoBill giver 里 16 个带**精确 defName 白名单**，mod 工作台一个都不在名单里
    /// ⇒ 原版对所有 giver 都返回 <c>false</c> ⇒ 代理永远取不到活（而菜单里却有配方，
    /// 因为那是我们自己的 <c>CraftUnlocks</c> 算的，不经过这道门）。</para>
    ///
    /// <para><b>为什么用作用域而不是全局改</b>：放宽会改变"真小人能做什么"的语义
    /// （原版就是靠白名单把烹饪台和切石台分开的）。这里只在代理调
    /// <c>JobOnThing</c> 的那一小段里打开，真小人的 WorkGiver 扫描完全不受影响。
    /// 同步单线程、无重入（取活循环里不会再调一次 <c>JobOnThing</c>），一个静态 bool 就够 ——
    /// 与 <see cref="BillAutomationScope"/> 同一套形态。</para>
    ///
    /// <para>开关在 <c>DigitalStorage.Settings.DigitalStorageSettings.craftAnyBench</c>（默认开）。</para>
    /// </summary>
    internal static class BenchWhitelistScope
    {
        /// <summary>true = 代理正在取活且这台台子没有任何原版 giver 认领 ⇒ 白名单按"恒真"处理。</summary>
        public static bool Relaxed;
    }
}
