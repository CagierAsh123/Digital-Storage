using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using RimWorld;
using Verse;

namespace DigitalStorage.Core
{
    /// <summary>
    /// <b>4.0 的账本替身</b>：直接遍历地图上的 haul source 容器内容物。
    ///
    /// <para>3.0 里这一层是 <c>CoreLedger</c>（<c>(def, stuff)</c> → 数量的纯数据）。
    /// 4.0 里容器内容物是**真实的 Thing**，住在 <c>ThingOwner</c> 里，
    /// 所以"查库存"就是"遍历容器"——不需要索引、不需要 ghost、不会失同步。</para>
    ///
    /// <para><b>收集方式与原版对齐</b>：统一用
    /// <c>ThingOwnerUtility.GetAllThingsRecursively(source, list)</c>（递归进 holder 树），
    /// 和 <c>WorkGiver_DoBill.cs:487</c> 的做法一致 —— 这样嵌套容器（容器里的容器）
    /// 也能被看见，且不会漏掉 <c>IThingHolder</c> 链上的东西。</para>
    ///
    /// <para><b>为什么不用 <c>listerThings</c></b>：容器内容物未 Spawned，本来就不在里面。
    /// 而硬把它塞进去是旧 <c>GhostThing</c> 的路 —— 未 Spawn 的东西被原版遍历到就 NRE 遍地。
    /// 详见 obsidian：<c>代码Wiki/csharp-api/容器内容物参与原版作业-ParentHolder返回Map.md</c></para>
    /// </summary>
    public static class HaulSourceContents
    {
        /// <summary>收集用的静态缓冲（这些方法都在"原版找不到 → 兜底"路径上，非热路径）。</summary>
        private static readonly List<Thing> tmpThings = new List<Thing>();
        private static readonly List<IHaulSource> tmpSources = new List<IHaulSource>();

        /// <summary>
        /// 跨图枚举用的**第二个**缓冲。必须与 <see cref="tmpSources"/> 分开：
        /// <c>FindBestIncludingRemote</c> 会先跑本图（<c>tmpSources</c>）再跑跨图，
        /// 共用一个缓冲会把本图那批冲掉（本 mod 早期就踩过静态缓冲互相覆盖的坑）。
        /// </summary>
        private static readonly List<IHaulSource> tmpRemoteSources = new List<IHaulSource>();

        /// <summary>本图所有启用中的 haul source。返回的列表是复用的静态缓冲，别存起来。</summary>
        public static List<IHaulSource> EnabledSources(Map map)
        {
            tmpSources.Clear();
            if (map == null) return tmpSources;

            List<IHaulSource> all = map.haulDestinationManager?.AllHaulSourcesListForReading;
            if (all == null) return tmpSources;

            for (int i = 0; i < all.Count; i++)
            {
                IHaulSource s = all[i];
                if (s != null && s.HaulSourceEnabled) tmpSources.Add(s);
            }
            return tmpSources;
        }

        // ===================================================================
        // 跨图（4.0 第三阶段）
        // ===================================================================
        //
        // 【作用域的形状】所有跨图入口都遵循同一条规则：**本图优先，本图拿不到才看别的图**。
        // 这不是限制，是排序 —— 本图有货时行为与 4.0 发布版逐字相同（零回归），
        // 本图没货时才有新行为。同时它也把跨图扫描的成本锁在"原本会失败"的那些调用上。

        /// <summary>
        /// <b>其它地图</b>上、处于可用状态的本 mod 核心。返回**复用缓冲**，别存起来。
        ///
        /// <para><b>只认本 mod 的核心</b>：跨图是 mod 自己的特性；原版容器（书架 / 衣架 /
        /// 自动工作台内胆）被跨图取用会破坏它们各自的语义（它们的内容物有下游 job 契约），
        /// 也会把别的 mod 的容器内容物拖进本 mod 的作业里。</para>
        /// </summary>
        public static List<IHaulSource> RemoteCoreSources(Map exclude)
        {
            tmpRemoteSources.Clear();
            List<Map> maps = Find.Maps;
            if (maps == null) return tmpRemoteSources;

            for (int i = 0; i < maps.Count; i++)
            {
                Map m = maps[i];
                if (m == null || m == exclude) continue;

                List<IHaulSource> all = m.haulDestinationManager?.AllHaulSourcesListForReading;
                if (all == null) continue;

                for (int j = 0; j < all.Count; j++)
                {
                    Building_StorageCore core = all[j] as Building_StorageCore;
                    if (core == null) continue;
                    if (!core.HaulSourceEnabled) continue;
                    if (!core.IsUsableNow) continue;
                    tmpRemoteSources.Add(core);
                }
            }
            return tmpRemoteSources;
        }

        /// <summary>别的图上有没有"装得下东西"的可用核心（选材菜单 / 缺料提示用）。</summary>
        public static bool AnyRemoteContent(Map exclude)
        {
            List<IHaulSource> sources = RemoteCoreSources(exclude);
            for (int i = 0; i < sources.Count; i++)
            {
                ThingOwner held = sources[i].GetDirectlyHeldThings();
                if (held != null && held.Count > 0) return true;
            }
            return false;
        }

        /// <summary>
        /// 本图**可用状态的本 mod 核心**（拍板：代理建筑只从核心取料，不去掏原版书架/衣架的库存）。
        /// 返回**复用缓冲**，别存起来 —— 刻意与 <see cref="tmpSources"/> 分开（本 mod 踩过静态缓冲互相覆盖的坑）。
        /// </summary>
        private static readonly List<IHaulSource> tmpCoreSources = new List<IHaulSource>();

        public static List<IHaulSource> CoreSources(Map map)
        {
            tmpCoreSources.Clear();
            if (map == null) return tmpCoreSources;

            List<IHaulSource> all = map.haulDestinationManager?.AllHaulSourcesListForReading;
            if (all == null) return tmpCoreSources;

            for (int i = 0; i < all.Count; i++)
            {
                Building_StorageCore core = all[i] as Building_StorageCore;
                if (core == null) continue;
                if (!core.HaulSourceEnabled) continue;
                if (!core.IsUsableNow) continue;
                tmpCoreSources.Add(core);
            }
            return tmpCoreSources;
        }

        /// <summary>本图容器里有没有东西。</summary>
        public static bool AnyContent(Map map)
        {
            List<IHaulSource> sources = EnabledSources(map);
            for (int i = 0; i < sources.Count; i++)
            {
                ThingOwner held = sources[i].GetDirectlyHeldThings();
                if (held != null && held.Count > 0) return true;
            }
            return false;
        }

        /// <summary>把本图所有容器内容物**递归**收集进 <paramref name="outThings"/>（先清空）。</summary>
        public static void GatherAll(Map map, List<Thing> outThings)
        {
            outThings.Clear();
            List<IHaulSource> sources = EnabledSources(map);
            for (int i = 0; i < sources.Count; i++)
            {
                tmpThings.Clear();
                ThingOwnerUtility.GetAllThingsRecursively(sources[i], tmpThings);
                for (int j = 0; j < tmpThings.Count; j++)
                {
                    Thing t = tmpThings[j];
                    if (t != null && !t.Destroyed) outThings.Add(t);
                }
            }
            tmpThings.Clear();
        }

        /// <summary>
        /// 收集容器的**直接**内容物（**不**递归进嵌套 holder）。
        ///
        /// <para>与 <see cref="GatherAll"/> 的区别很重要，用错会出真问题：
        /// <c>GatherAll</c> 走 <c>ThingOwnerUtility.GetAllThingsRecursively</c>，会递归进嵌套 holder
        /// —— 于是一个 <c>Corpse</c> 会连它里面的 <c>Pawn</c> 一起被交出来、一个
        /// <c>MinifiedThing</c> 会把里面的 <c>Building</c> 交出来。</para>
        ///
        /// <para>对"工作台找原料 / 建造选材"这类场景，递归是**对的**（原版
        /// <c>WorkGiver_DoBill:487</c> 也用递归）。但**交易列表不能这样**：原版自己的容器分支
        /// （<c>Building_Bookcase.HeldBooks</c> / <c>Building_OutfitStand.HeldItems</c>）
        /// 都只取**直接**内容物，绝不会把尸体里的 pawn 当成可交易物。</para>
        /// </summary>
        public static void CollectDirect(Map map, List<Thing> outThings)
        {
            outThings.Clear();
            List<IHaulSource> sources = EnabledSources(map);
            for (int i = 0; i < sources.Count; i++)
            {
                ThingOwner held = sources[i].GetDirectlyHeldThings();
                if (held == null) continue;
                for (int j = 0; j < held.Count; j++)
                {
                    Thing t = held[j];
                    if (t != null && !t.Destroyed) outThings.Add(t);
                }
            }
        }

        /// <summary>
        /// 在容器里找 <paramref name="score"/> 最大且通过 <paramref name="validator"/> 的东西。
        /// 找不到返回 null。<paramref name="score"/> 可以是 null（等价于都取 0，返回第一个通过的）。
        ///
        /// <para>只搜**本图**。要跨图见 <see cref="FindBestIncludingRemote"/>。</para>
        /// </summary>
        public static Thing FindBest(Map map, Func<Thing, float> score, Predicate<Thing> validator)
        {
            if (map == null || validator == null) return null;
            return FindBestIn(EnabledSources(map), score, validator);
        }

        /// <summary>
        /// <b>先本图、再其它图</b>的 <see cref="FindBest"/>。本图找到就立刻返回 ——
        /// 本图有货时与只搜本图**完全等价**，跨图那段代码一次都不跑。
        /// </summary>
        public static Thing FindBestIncludingRemote(Map map, Func<Thing, float> score, Predicate<Thing> validator)
        {
            if (map == null || validator == null) return null;

            Thing local = FindBestIn(EnabledSources(map), score, validator);
            if (local != null) return local;

            return FindBestIn(RemoteCoreSources(map), score, validator);
        }

        /// <summary>两个作用域共用的选取内核。<paramref name="sources"/> 是调用方给的缓冲。</summary>
        private static Thing FindBestIn(List<IHaulSource> sources, Func<Thing, float> score, Predicate<Thing> validator)
        {
            Thing best = null;
            float bestScore = float.MinValue;

            for (int i = 0; i < sources.Count; i++)
            {
                tmpThings.Clear();
                ThingOwnerUtility.GetAllThingsRecursively(sources[i], tmpThings);
                for (int j = 0; j < tmpThings.Count; j++)
                {
                    Thing t = tmpThings[j];
                    if (t == null || t.Destroyed || !validator(t)) continue;

                    float s = (score != null) ? score(t) : 0f;
                    if (best == null || s > bestScore)
                    {
                        best = t;
                        bestScore = s;
                    }
                }
            }
            tmpThings.Clear();
            return best;
        }

        /// <summary>本图 + 其它图核心的**递归**内容物（供"缺料提示 / 选材菜单 / 右键取出"枚举）。</summary>
        public static void GatherAllIncludingRemote(Map map, List<Thing> outThings)
        {
            GatherAll(map, outThings);
            AppendRemote(map, outThings);
        }

        /// <summary>把其它图核心的递归内容物追加进 <paramref name="outThings"/>（不清空）。</summary>
        public static void AppendRemote(Map exclude, List<Thing> outThings)
        {
            if (outThings == null) return;

            List<IHaulSource> sources = RemoteCoreSources(exclude);
            for (int i = 0; i < sources.Count; i++)
            {
                tmpThings.Clear();
                ThingOwnerUtility.GetAllThingsRecursively(sources[i], tmpThings);
                for (int j = 0; j < tmpThings.Count; j++)
                {
                    Thing t = tmpThings[j];
                    if (t != null && !t.Destroyed) outThings.Add(t);
                }
            }
            tmpThings.Clear();
        }

        /// <summary>容器里某 def 的总数量。</summary>
        public static int CountOf(Map map, ThingDef def)
        {
            if (map == null || def == null) return 0;

            int total = 0;
            List<IHaulSource> sources = EnabledSources(map);
            for (int i = 0; i < sources.Count; i++)
            {
                tmpThings.Clear();
                ThingOwnerUtility.GetAllThingsRecursively(sources[i], tmpThings);
                for (int j = 0; j < tmpThings.Count; j++)
                {
                    Thing t = tmpThings[j];
                    if (t != null && !t.Destroyed && t.def == def) total += t.stackCount;
                }
            }
            tmpThings.Clear();
            return total;
        }

        /// <summary>
        /// 把 <paramref name="t"/>（住在容器里）取出 <paramref name="count"/> 个，
        /// 落到 <paramref name="pawn"/> 脚下并返回落地的那个 Thing。失败返回 null（物品留在原处）。
        ///
        /// <para><b>为什么是"取出放脚下"而不是"让原版自己走到容器里拿"</b>：
        /// 消耗这条链的原版 job（<c>JobDriver_Refuel</c> / <c>JobDriver_FixBrokenDownBuilding</c> /
        /// <c>JobDriver_TendPatient</c> / <c>JobDriver_Ingest</c>）**没有 <c>canGotoSpawnedParent</c>**
        /// —— 原版只给 <c>JobDriver_DoBill:121</c> / <c>HaulToCell:114</c> / <c>HaulToContainer:137</c>
        /// 等少数几条装了它。所以对这几条链，原版根本走不到容器内容物，
        /// 只能由我们把人参换成"脚边一个真东西"，原版 job 语义一字不改。
        /// （详见 obsidian 4.0-乙-原版可见性判定表 第八节）</para>
        ///
        /// <para><c>MarkWithdrawn</c>：给自动收纳一个 300 tick 的保护窗口，
        /// 免得刚取出来就被瞬移吸回去，形成"取—吸"死循环。</para>
        /// </summary>
        public static Thing ExtractToFeet(Thing t, int count, Pawn pawn)
        {
            if (pawn == null) return null;
            return ExtractTo(t, count, pawn.Position, pawn.Map, out _);
        }

        /// <summary>
        /// <see cref="ExtractToFeet"/> 的通用形态：取出后落到 <paramref name="pos"/>。
        /// 与 pawn 解耦，供 ITab / 其它非 job 场景复用。
        /// </summary>
        public static Thing ExtractTo(Thing t, int count, IntVec3 pos, Map map, out int extracted, bool forbid = false)
        {
            extracted = 0;
            if (t == null || t.Destroyed || map == null) return null;
            if (count <= 0 || !pos.IsValid) return null;

            IThingHolder holder = t.ParentHolder as IThingHolder;
            if (holder == null) return null;
            Building_StorageCore core = holder as Building_StorageCore;
            if (core != null && !core.HaulSourceEnabled) return null;
            ThingOwner owner = holder.GetDirectlyHeldThings();
            if (owner == null || !owner.Contains(t)) return null;

            int take = Math.Min(count, t.stackCount);
            if (take <= 0) return null;

            Thing taken;
            if (take >= t.stackCount)
            {
                // 整堆取走
                owner.Remove(t);
                taken = t;
            }
            else
            {
                // 拆一部分出来。SplitOff 不触碰 owner，原堆留在容器里且数量已减。
                taken = t.SplitOff(take);
            }
            if (taken == null) return null;

            // 落地可能合堆, 数量与存活目标必须分开记录
            Thing placed = null;
            GenPlace.TryPlaceThing(taken, pos, map, ThingPlaceMode.Near, out Thing lastPlaced,
                (result, amount) =>
                {
                    placed = result;
                    Components.CompAutoIngest.MarkWithdrawn(result);
                    if (forbid) result.SetForbidden(true, false);
                });
            int remaining = taken.Destroyed ? 0 : (taken.holdingOwner == null && !taken.Spawned ? taken.stackCount : 0);
            extracted = take - remaining;
            if (remaining > 0 && !owner.TryAdd(taken, true))
                GenPlace.TryPlaceThing(taken, pos, map, ThingPlaceMode.Near);
            return extracted > 0 ? placed ?? lastPlaced : null;
        }

        /// <summary>
        /// 从容器里凑齐 <paramref name="count"/> 个「满足 <paramref name="filter"/>」的东西，
        /// 逐个落到 <paramref name="pos"/>，返回实际取出的总数。
        ///
        /// <para>用于 ITab 面板的"取出"按钮 —— 原版 UI 不知道我们的容器，
        /// 而 job 路径只认「一件 Thing」，凑多堆得在这里做。</para>
        ///
        /// <para><paramref name="filter"/> 让面板能精确到"哪一批"（def + stuff + 品质 + 耐久），
        /// 而不是只按 def 粗取 —— 「全放开」之后同一 def 可能同时有普通剑和传奇剑。</para>
        ///
        /// <para><paramref name="forbid"/>：取出后设为禁止（3.0 语义）。防止刚取出来就被
        /// 原版搬运工或自动收纳送回去，形成"取—送"死循环。</para>
        /// </summary>
        public static int ExtractMatchingTo(Building_StorageCore core, int count, IntVec3 pos, Map map,
            Predicate<Thing> filter, bool forbid = true)
        {
            if (core == null || !core.HaulSourceEnabled || filter == null || count <= 0 || map == null) return 0;

            int got = 0;
            while (got < count)
            {
                Thing next = null;
                ThingOwner held = core.GetDirectlyHeldThings();
                for (int i = 0; i < held.Count; i++)
                {
                    Thing candidate = held[i];
                    if (candidate == null || candidate.Destroyed || !filter(candidate)) continue;
                    if (next == null || candidate.stackCount > next.stackCount) next = candidate;
                }
                if (next == null) break;

                int want = Math.Min(count - got, next.stackCount);
                ExtractTo(next, want, pos, map, out int extracted, forbid);
                if (extracted <= 0) break;
                got += extracted;
            }
            return got;
        }

    }
}
