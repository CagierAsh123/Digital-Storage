using System.Collections.Generic;
using DigitalStorage.Components;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// <b>工作台认领表</b>：一台工作台同一时间只归**一个**制作代理。
    ///
    /// <para><b>为什么必须有</b>：代理干活是"脱离 job 直调产出"，<b>不做真预约</b>
    /// （真预约需要真 job，而假 job 会被写进存档、读档找不回来 —— 见
    /// <c>CompDigitalWorker</c> 里的 <c>DigitalWorkerClaims</c>）。不做预约就有两个后果：</para>
    /// <list type="number">
    /// <item>两台制作代理的 13×13 范围重叠时，会**同时**抓同一张台子的同一条 bill
    ///   ⇒ 双重推进、双重产出（RepeatCount 会被 <c>if (repeatCount &gt; 0) repeatCount--</c> 兜住，
    ///   但 <c>Forever</c> 与 <c>TargetCount</c> 会多做）；</item>
    /// <item>"并发 = 台数"这条语义会被破坏：同一张台子被数两次就变成并发 2。</item>
    /// </list>
    ///
    /// <para>认领表**不进存档**：读档后所有代理重新扫描即可（谁先扫到谁拿）。
    /// 与 <c>DigitalWorkerClaims</c> 同构，但键是工作台、值是 comp。</para>
    /// </summary>
    internal static class BillBenchClaims
    {
        private static readonly Dictionary<Map, Dictionary<Thing, CompBillAutomation>> claims =
            new Dictionary<Map, Dictionary<Thing, CompBillAutomation>>();

        private static Dictionary<Thing, CompBillAutomation> For(Map map)
        {
            Dictionary<Thing, CompBillAutomation> d;
            if (!claims.TryGetValue(map, out d))
            {
                d = new Dictionary<Thing, CompBillAutomation>();
                claims[map] = d;
            }
            return d;
        }

        /// <summary>这台工作台现在归谁（没人认领返回 null）。</summary>
        public static CompBillAutomation OwnerOf(Map map, Thing bench)
        {
            if (map == null || bench == null) return null;
            Dictionary<Thing, CompBillAutomation> d;
            if (!claims.TryGetValue(map, out d)) return null;
            CompBillAutomation owner;
            return d.TryGetValue(bench, out owner) ? owner : null;
        }

        /// <summary>认领（已被别人占了则返回 false）。</summary>
        public static bool TryClaim(Map map, Thing bench, CompBillAutomation me)
        {
            if (map == null || bench == null || me == null) return false;
            Dictionary<Thing, CompBillAutomation> d = For(map);
            CompBillAutomation owner;
            if (d.TryGetValue(bench, out owner) && owner != null && owner != me) return false;
            d[bench] = me;
            return true;
        }

        public static void Release(Map map, Thing bench, CompBillAutomation me)
        {
            if (map == null || bench == null) return;
            Dictionary<Thing, CompBillAutomation> d;
            if (!claims.TryGetValue(map, out d)) return;
            CompBillAutomation owner;
            if (d.TryGetValue(bench, out owner) && owner == me) d.Remove(bench);
            if (d.Count == 0) claims.Remove(map);
        }

        /// <summary>放手这个代理认领的全部工作台（断电/拆除/关掉时调）。</summary>
        public static void ReleaseAll(Map map, CompBillAutomation me)
        {
            if (map == null || me == null) return;
            Dictionary<Thing, CompBillAutomation> d;
            if (!claims.TryGetValue(map, out d)) return;

            tmp.Clear();
            foreach (KeyValuePair<Thing, CompBillAutomation> kv in d)
            {
                if (kv.Value == me) tmp.Add(kv.Key);
            }
            for (int i = 0; i < tmp.Count; i++)
            {
                d.Remove(tmp[i]);
            }
            tmp.Clear();
            if (d.Count == 0) claims.Remove(map);
        }

        private static readonly List<Thing> tmp = new List<Thing>();

        /// <summary>清空整表（读档 / 换档；键是 <c>Map</c> 与 <c>Thing</c> 实例，读档后全是死引用）。</summary>
        public static void ClearAll()
        {
            claims.Clear();
            tmp.Clear();
        }
    }
}
