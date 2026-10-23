using System.Collections.Generic;
using Verse;

namespace DigitalStorage.AI
{
    /// <summary>
    /// <b>制作代理的原料认领表</b>（审计 2026-10-17 · B1，对应第三方补丁 FIX-13）。
    ///
    /// <para><b>为什么必须有</b>：制作代理不跑真 job（假 pawn 直调产出），所以**不做真预约**。
    /// 后果是几条线同时取活时，原版选料器会把**同一堆料**分别记给每一条线：
    /// 谁先做完谁扣料，其余线下一次 <c>StillValid</c> 发现数量对不上 ⇒ 整轮作废重取。
    /// 表现就是「并行塌缩成串行」—— 台子越多越明显（三份独立审计同时命中这条）。</para>
    ///
    /// <para><b>为什么不用 <c>ReservationManager</c></b>：预约要求 (thing, pawn) 二元组，
    /// 而代理只有一个共享的资质载体 pawn（<see cref="Components.CompBillAutomation.Worker"/>）。
    /// 同一 pawn 对同一堆料再预约一次会被原版拒绝 ⇒ 还是塌缩。这里用"认领"而不是"预约"：
    /// 只在本 mod 自己的调度里互斥，同时**不与真人抢**（真人占用由 <c>StillValid</c> 的
    /// <c>reservationManager.IsReservedByAnyoneOf</c> 单独判定）。</para>
    ///
    /// <para><b>按量认领（2026-10-23 修，实机 V-B0-5）</b>：早期实现是 <c>Dictionary&lt;Thing, object&gt;</c>
    /// —— <b>整堆互斥</b>。于是「两条不同配方都从同一堆钢里取料」时，第二条线被判
    /// 「这批材料正被另一条产线占用」而永远排不上 ⇒ 两张台子只能串行。
    /// 现在每堆料记一张 <c>Claim{Owner, Amount}</c> 列表，只要求
    /// <c>Σ Amount ≤ stackCount</c>：一堆 75 件钢可以 60 给线 A、15 给线 B。
    /// 取活时若原版挑中的那堆不够，调用方会从**同 def 的其它堆**补齐（可跨多堆拆分，
    /// 结算侧 <c>BillCraftFunnel</c> 本来就用 <c>SplitOff</c> 按量切料，配合得上）。</para>
    ///
    /// <para><b>不进存档</b>：读档后所有线重建、重新取活。跨档残留由
    /// <c>Services.DigitalStorageGameComponent.FinalizeInit</c> 调 <see cref="Clear"/> 清掉。</para>
    /// </summary>
    internal static class CraftIngredientClaims
    {
        /// <summary>某条线在一堆料上认领的件数。</summary>
        private sealed class Claim
        {
            public object Owner;
            public int Amount;
        }

        private static readonly Dictionary<Thing, List<Claim>> claims = new Dictionary<Thing, List<Claim>>();

        private static readonly List<Thing> tmp = new List<Thing>();

        private static int callsSinceSweep;

        /// <summary>这堆料现在归谁（多线按量共享时返回其中一条；没人认领返回 null）。</summary>
        public static object OwnerOf(Thing t)
        {
            if (t == null) return null;
            List<Claim> list;
            if (!claims.TryGetValue(t, out list) || list == null) return null;
            for (int i = 0; i < list.Count; i++)
            {
                Claim c = list[i];
                if (c != null && c.Owner != null) return c.Owner;
            }
            return null;
        }

        /// <summary>这堆料上**所有**线一共认领了多少件（含自己）。</summary>
        public static int ClaimedTotal(Thing t)
        {
            if (t == null) return 0;
            List<Claim> list;
            if (!claims.TryGetValue(t, out list) || list == null) return 0;
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Claim c = list[i];
                if (c != null) n += c.Amount;
            }
            return n;
        }

        /// <summary>这堆料还剩多少件可以被认领（= 堆数 − 已认领，下限 0）。</summary>
        public static int FreeAmountOf(Thing t)
        {
            if (t == null || t.Destroyed) return 0;
            int free = t.stackCount - ClaimedTotal(t);
            return free > 0 ? free : 0;
        }

        /// <summary>这堆料上有没有**别人**的认领（用于把"被占用"与"真缺料"分开报）。</summary>
        public static bool HasOtherOwner(Thing t, object owner)
        {
            if (t == null) return false;
            List<Claim> list;
            if (!claims.TryGetValue(t, out list) || list == null) return false;
            for (int i = 0; i < list.Count; i++)
            {
                Claim c = list[i];
                if (c != null && c.Owner != null && !ReferenceEquals(c.Owner, owner)) return true;
            }
            return false;
        }

        /// <summary>
        /// 认领 <paramref name="amount"/> 件。<paramref name="owner"/> 自己已有认领时可以累加；
        /// 总和超过堆数则返回 false（调用方自己退让或改从别的堆取）。
        /// </summary>
        public static bool TryClaim(Thing t, object owner, int amount)
        {
            if (t == null || t.Destroyed || owner == null || amount <= 0) return false;

            // 低频清扫：料被吃掉/销毁/合成之后引用会留在表里（只是内存卫生，不影响判据）。
            if (++callsSinceSweep >= 400)
            {
                callsSinceSweep = 0;
                Sweep();
            }

            List<Claim> list;
            if (!claims.TryGetValue(t, out list) || list == null)
            {
                list = new List<Claim>(2);
                claims[t] = list;
            }

            int taken = 0;
            int own = -1;
            for (int i = 0; i < list.Count; i++)
            {
                Claim c = list[i];
                if (c == null) continue;
                taken += c.Amount;
                if (ReferenceEquals(c.Owner, owner)) own = i;
            }

            if (taken + amount > t.stackCount) return false;

            if (own >= 0)
            {
                list[own].Amount += amount;
                return true;
            }

            list.Add(new Claim { Owner = owner, Amount = amount });
            return true;
        }

        /// <summary>认领这堆料的**全部余量**（已经被别的线占满则 false）。</summary>
        public static bool TryClaim(Thing t, object owner)
        {
            if (t == null) return false;
            return TryClaim(t, owner, FreeAmountOf(t));
        }

        /// <summary>放开这个 owner 名下全部认领（线作废/完成/释放时调）。</summary>
        public static void Release(object owner)
        {
            if (owner == null) return;
            tmp.Clear();
            foreach (KeyValuePair<Thing, List<Claim>> kv in claims)
            {
                List<Claim> list = kv.Value;
                if (list == null)
                {
                    tmp.Add(kv.Key);
                    continue;
                }

                for (int i = list.Count - 1; i >= 0; i--)
                {
                    Claim c = list[i];
                    if (c == null || ReferenceEquals(c.Owner, owner)) list.RemoveAt(i);
                }
                if (list.Count == 0) tmp.Add(kv.Key);
            }
            for (int i = 0; i < tmp.Count; i++)
            {
                if (tmp[i] != null) claims.Remove(tmp[i]);
            }
            tmp.Clear();
        }

        /// <summary>清空（换档/读档）。</summary>
        public static void Clear()
        {
            claims.Clear();
            tmp.Clear();
            callsSinceSweep = 0;
        }

        private static void Sweep()
        {
            tmp.Clear();
            foreach (KeyValuePair<Thing, List<Claim>> kv in claims)
            {
                Thing t = kv.Key;
                if (t == null || t.Destroyed || t.holdingOwner == null)
                {
                    tmp.Add(t);
                    continue;
                }

                List<Claim> list = kv.Value;
                if (list == null || list.Count == 0)
                {
                    tmp.Add(t);
                    continue;
                }

                // 线已经放手（ClearAll 把 Bench 置空）却还有认领 ⇒ 是异常路径漏掉的，收掉。
                // 注意**不能**用 Ingredients == null 判：取活过程中它本来就是 null。
                bool stale = false;
                for (int i = 0; i < list.Count; i++)
                {
                    Claim c = list[i];
                    if (c == null) continue;
                    CraftLine line = c.Owner as CraftLine;
                    if (line != null && line.Bench == null) { stale = true; break; }
                }
                if (stale)
                {
                    tmp.Add(t);
                    continue;
                }

                // 堆变小了（被真人/别的线拿走几件）：把认领按顺序夹回堆数，
                // 免得"账上还欠 60 件、实际只有 20 件"。数量对不上由 StillValid 兜底作废。
                int budget = t.stackCount;
                for (int i = 0; i < list.Count; i++)
                {
                    Claim c = list[i];
                    if (c == null) continue;
                    if (budget <= 0)
                    {
                        list.RemoveAt(i);
                        i--;
                        continue;
                    }
                    if (c.Amount > budget) c.Amount = budget;
                    budget -= c.Amount;
                }
                if (list.Count == 0) tmp.Add(t);
            }
            for (int i = 0; i < tmp.Count; i++)
            {
                if (tmp[i] != null) claims.Remove(tmp[i]);
            }
            tmp.Clear();
        }
    }
}
