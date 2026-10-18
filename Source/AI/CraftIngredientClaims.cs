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
    /// <para><b>不进存档</b>：读档后所有线重建、重新取活。跨档残留由
    /// <c>Services.DigitalStorageGameComponent.FinalizeInit</c> 调 <see cref="Clear"/> 清掉。</para>
    /// </summary>
    internal static class CraftIngredientClaims
    {
        private static readonly Dictionary<Thing, object> claims = new Dictionary<Thing, object>();

        private static readonly List<Thing> tmp = new List<Thing>();

        private static int callsSinceSweep;

        /// <summary>这堆料现在归谁（没有认领返回 null）。</summary>
        public static object OwnerOf(Thing t)
        {
            if (t == null) return null;
            object owner;
            return claims.TryGetValue(t, out owner) ? owner : null;
        }

        /// <summary>认领一堆料。已被**别的**线认领则返回 false（同一 owner 可重复认领）。</summary>
        public static bool TryClaim(Thing t, object owner)
        {
            if (t == null || owner == null) return false;

            // 低频清扫：料被吃掉/销毁/合成之后引用会留在表里（只是内存卫生，不影响判据）。
            if (++callsSinceSweep >= 400)
            {
                callsSinceSweep = 0;
                Sweep();
            }

            object current;
            if (claims.TryGetValue(t, out current) && current != null && !ReferenceEquals(current, owner))
                return false;

            claims[t] = owner;
            return true;
        }

        /// <summary>放开这个 owner 名下全部认领（线作废/完成/释放时调）。</summary>
        public static void Release(object owner)
        {
            if (owner == null) return;
            tmp.Clear();
            foreach (KeyValuePair<Thing, object> kv in claims)
            {
                if (ReferenceEquals(kv.Value, owner)) tmp.Add(kv.Key);
            }
            for (int i = 0; i < tmp.Count; i++) claims.Remove(tmp[i]);
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
            foreach (KeyValuePair<Thing, object> kv in claims)
            {
                Thing t = kv.Key;
                CraftLine line = kv.Value as CraftLine;

                // 线已经放手（ClearAll 把 Bench 置空）却还有认领 ⇒ 是异常路径漏掉的，收掉。
                // 注意**不能**用 Ingredients == null 判：取活过程中它本来就是 null。
                if (t == null || t.Destroyed || t.holdingOwner == null || (line != null && line.Bench == null))
                    tmp.Add(t);
            }
            for (int i = 0; i < tmp.Count; i++)
            {
                if (tmp[i] != null) claims.Remove(tmp[i]);
            }
            tmp.Clear();
        }
    }
}
