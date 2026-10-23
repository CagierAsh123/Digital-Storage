using System.Collections.Generic;
using System.Text;
using DigitalStorage.AI;
using DigitalStorage.Core;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// <b>V-B0-4（A2「头衔/戒律口径」）的场景生成器</b>：一条命令造出"有头衔、且有文化的殖民者"，
    /// 并把与头衔冲突的食物**只**放进最近的核心。
    ///
    /// <para><b>为什么要这个工具</b>：手工准备这个场景非常麻烦 —— 要让
    /// <c>FoodUtility.InappropriateForTitle</c>（<c>FoodUtility.cs:159-179</c>）真的生效，必须同时满足四件事：</para>
    /// <list type="number">
    /// <item>小人阵营是玩家派系 <b>且不是任务寄居者</b>；</item>
    /// <item>名下有 <c>RoyalTitle.conceited == true</c> 的头衔（高阶帝国头衔才带 <c>foodRequirement</c>）；</item>
    /// <item><b>玩家派系的头衔不会自动 conceited</b> —— <c>RoyalTitleUtility.ShouldBecomeConceitedOnNewTitle</c>
    ///   （<c>:437-449</c>）要求"有傲慢类特质"（<c>Abrasive</c> / <c>Greedy</c> / <c>Jealous</c>），
    ///   否则这个人在原版眼里根本不傲慢，头衔的食物要求**静默失效**（测试会假绿）；</item>
    /// <item>食物必须<b>不是</b>「饥饿到 <c>Starving</c>」时被判断 ——
    ///   <c>InappropriateForTitle(..., allowIfStarving: true)</c> 会直接放行。饿死边缘的人吃什么都不挑。</item>
    /// </list>
    ///
    /// <para>所以本工具按顺序做：生成殖民者 → 补傲慢特质 → 授帝国头衔（再兜底强制 <c>conceited</c>）→
    /// 确认文化（Ideology）有戒律 → 把饥饿度压到「饿但不饿死」→ 把冲突食物塞进最近的核心。</para>
    ///
    /// <para><b>判据只打原版结果，不复刻判定</b>：表里每一列都是真的调用了那个原版方法之后的返回值
    /// （<c>InappropriateForTitle</c> / <c>IsVeneratedAnimalMeatOrCorpse</c> / <c>WillEat</c>），
    /// 与 <c>Patch_FeedFromStorage</c> 里抽取谓词用的是同一个方法 —— 这样"表说不会抽"
    /// 与"实际没抽"才是同一件事。</para>
    /// </summary>
    internal static class TitleFoodDevTool
    {
        private const string Category = "DigitalStorage";

        /// <summary>
        /// 把饥饿度压到这个比例。<c>Need_Food.CurCategory</c>（<c>Need_Food.cs:29-47</c>）里
        /// <c>Starving</c> 只在 <c>CurLevelPercentage &lt;= 0</c> 时成立，而
        /// <c>PercentageThreshUrgentlyHungry</c> = <c>FoodLevelPercentageWantEat * 0.4</c>（人类约 0.2）
        /// ⇒ 0.15 是「急饿」但**没饿死**，既会立刻去找吃的，又不会触发 <c>allowIfStarving</c> 的放行。
        /// </summary>
        private const float HungryLevelPercentage = 0.15f;

        /// <summary>候选食物都用 defName 查（<c>GetNamedSilentFail</c>），缺 DLC 的候选自动跳过，不会抛。</summary>
        private static readonly string[] ConflictCandidates =
        {
            "RawPotato", "RawRice", "RawCorn", "MealSurvivalPack"
        };

        private static readonly string[] CompliantCandidates =
        {
            "MealSimple", "Pemmican", "MealFine", "RawBerries", "MealNutrientPaste", "MealLavish"
        };

        private static readonly StringBuilder SB = new StringBuilder();

        // ===================================================================
        // 入口 1：生成场景（点地上任意格子）
        // ===================================================================

        [DebugAction(Category, "V-B0-4-1 生成「有头衔」殖民者 + 冲突食物只放核心（点格子）",
            actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SetupVariant()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;

            // ⚠️ 必须写全 `Verse.UI` —— 本 mod 自己有个 DigitalStorage.UI 命名空间
            IntVec3 cell = Verse.UI.MouseCell();
            if (!cell.InBounds(map)) return;

            MoteMaker.ThrowText(cell.ToVector3Shifted(), map, "DS V-B0-4 ← 生成");

            Pawn pawn = SpawnColonist(cell, map);
            if (pawn == null) return;

            MakeConceited(pawn);
            Pawn ideoOk = EnsureIdeo(pawn) ? pawn : null;
            RoyalTitle title = GrantConceitedTitle(pawn);
            MakeHungry(pawn);

            SB.Length = 0;
            SB.AppendLine("=== [DS V-B0-4] 场景已生成：" + pawn.LabelShort + " @ " + cell + " ===");
            AppendPawnState(pawn, title);
            if (ideoOk == null) SB.AppendLine("  ⚠️ 这个 pawn 现在没有文化（无 Ideology DLC 或 ideo 为空）");

            // 只把**冲突**食物放进核心 —— 这正是验收条目「与头衔冲突的食物只在核心」
            Building_StorageCore core = NearestCore(map, cell, out int coreCount);
            if (core == null)
            {
                SB.AppendLine("  ✗ 本图没有核心建筑：先造一个核心（通电）再点这个动作。");
                Log.Warning(SB.ToString());
                return;
            }

            ThingDef conflict = FindFood(pawn, ConflictCandidates, wantWillEat: false);
            if (conflict == null)
            {
                SB.AppendLine("  ✗ 找不到「会被这个 pawn 拒绝」的食物（头衔可能要没生效，先看上面的状态）。");
                Log.Warning(SB.ToString());
                return;
            }

            // 清空核心再放：如果核心原本有别的食物，"只剩冲突食物"这个前提就不成立了
            int cleared = ClearCore(core);
            SB.AppendLine("  核心 " + core.Position + "（全图 " + coreCount + " 个，已通电=" + core.IsUsableNow + "）"
                          + "：清掉原有 " + cleared + " 件");
            int stored = Stock(core, conflict, 20);
            SB.AppendLine("  放入冲突食物 = " + conflict.defName + " × " + stored
                          + "（WillEat=" + pawn.WillEat(conflict, pawn, careIfNotAcceptableForTitle: true) + "）");
            SB.AppendLine("  期望：这个人**不会**把这食物抽到脚下（Player.log 里也不该有抽取记录）；");
            SB.AppendLine("        地上/他身上都不出现这种食物。第二步用「V-B0-4-2」换成合规食物，则应立刻被抽取。");
            SB.AppendLine("  —— 食物口径全表 ——");
            AppendFoodTable(pawn);
            Log.Warning(SB.ToString());
        }

        // ===================================================================
        // 入口 2：把核心里的食物换成「合规」的（点同一个格子）
        // ===================================================================

        [DebugAction(Category, "V-B0-4-2 核心换成合规食物（点核心/它的格子）",
            actionType = DebugActionType.ToolMap,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void SwapToCompliant()
        {
            Map map = Find.CurrentMap;
            if (map == null) return;
            IntVec3 cell = Verse.UI.MouseCell();
            if (!cell.InBounds(map)) return;

            MoteMaker.ThrowText(cell.ToVector3Shifted(), map, "DS V-B0-4 ← 换合规食物");

            Building_StorageCore core = NearestCore(map, cell, out int coreCount);
            if (core == null)
            {
                Log.Warning("[DS V-B0-4] 本图没有核心建筑。");
                return;
            }

            // 找"最近的、有头衔的殖民者"当口径参照：验收本来就是这个人在测
            Pawn pawn = NearestTitledColonist(map, cell);
            if (pawn == null)
            {
                Log.Warning("[DS V-B0-4] 找不到「有头衔的殖民者」——先用 V-B0-4-1 生成一个，或点它的格子。");
                return;
            }

            SB.Length = 0;
            SB.AppendLine("=== [DS V-B0-4] 换成合规食物：目标 " + pawn.LabelShort + "  核心 " + core.Position + " ===");
            int cleared = ClearCore(core);
            ThingDef ok = FindFood(pawn, CompliantCandidates, wantWillEat: true);
            if (ok == null)
            {
                SB.AppendLine("  ✗ 找不到这个 pawn 会接受的食物（先看 V-B0-4-3 的全表）。");
                Log.Warning(SB.ToString());
                return;
            }
            int stored = Stock(core, ok, 20);
            SB.AppendLine("  清掉原有 " + cleared + " 件；放入合规食物 = " + ok.defName + " × " + stored
                          + "（WillEat=" + pawn.WillEat(ok, pawn, careIfNotAcceptableForTitle: true) + "）");
            SB.AppendLine("  期望：这个人现在**会**把食物抽到脚下并吃掉（这就是对照）。");
            AppendFoodTable(pawn);
            Log.Warning(SB.ToString());
        }

        // ===================================================================
        // 入口 3：看某个 pawn 的食物口径全表（点 pawn）
        // ===================================================================

        [DebugAction(Category, "V-B0-4-3 这个 pawn 的食物口径全表（点 pawn）",
            actionType = DebugActionType.ToolMapForPawns,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DumpFoodTableForPawn(Pawn p)
        {
            if (p == null || p.Map == null) return;
            MoteMaker.ThrowText(p.DrawPos, p.Map, "DS V-B0-4 ← 口径表");

            SB.Length = 0;
            SB.AppendLine("=== [DS V-B0-4] 食物口径全表：" + p.LabelShort + " ===");
            AppendPawnState(p, p.royalty?.MostSeniorTitle);
            AppendFoodTable(p);
            AppendCoreContents(p);
            Log.Warning(SB.ToString());
        }

        // ===================================================================
        // 生成 / 特质 / 文化 / 头衔 / 饥饿
        // ===================================================================

        /// <summary>
        /// 生成一个**玩家派系**殖民者并落地。
        /// <para>用 <c>PawnKindDefOf.Colonist</c> + <c>Faction.OfPlayer</c>（和原版
        /// <c>DebugToolsSpawning</c> 的生成路径一致）；<c>GenSpawn.Spawn</c> 会把它登记进
        /// <c>map.mapPawns</c>，玩家派系 ⇒ 自动是殖民地成员，能被 <c>JobGiver_GetFood</c> 派活。</para>
        /// </summary>
        private static Pawn SpawnColonist(IntVec3 cell, Map map)
        {
            Pawn pawn;
            try
            {
                pawn = PawnGenerator.GeneratePawn(new PawnGenerationRequest(
                    PawnKindDefOf.Colonist,
                    Faction.OfPlayer,
                    PawnGenerationContext.NonPlayer,
                    map.Tile,
                    forceGenerateNewPawn: true,
                    canGeneratePawnRelations: false));
            }
            catch (System.Exception e)
            {
                Log.Error("[DS V-B0-4] 生成殖民者失败：" + e);
                return null;
            }
            if (pawn == null) return null;

            GenSpawn.Spawn(pawn, cell, map, Rot4.South);
            pawn.workSettings?.EnableAndInitializeIfNotAlreadyInitialized();
            Find.ColonistBar?.MarkColonistsDirty();
            return pawn;
        }

        /// <summary>
        /// 保证"傲慢"：玩家派系的头衔是否 conceited 由
        /// <c>RoyalTitleUtility.IsPawnConceited</c>（<c>:369-389</c>）= <c>Abrasive || Greedy || Jealous</c>（且非 Ascetic）决定。
        /// <para>先清 Ascetic（它一票否决），再补一个 Greedy。</para>
        /// </summary>
        private static void MakeConceited(Pawn pawn)
        {
            TraitSet traits = pawn.story?.traits;
            if (traits == null) return;

            if (traits.HasTrait(TraitDefOf.Ascetic))
            {
                Trait ascetic = traits.GetTrait(TraitDefOf.Ascetic);
                if (ascetic != null) traits.RemoveTrait(ascetic, unsuppressConflicts: true);
            }
            if (traits.HasTrait(TraitDefOf.Greedy) || traits.HasTrait(TraitDefOf.Abrasive) || traits.HasTrait(TraitDefOf.Jealous))
                return;
            traits.GainTrait(new Trait(TraitDefOf.Greedy), suppressConflicts: true);
        }

        /// <summary>
        /// 确认文化（Ideology）存在且有戒律。没有 Ideology DLC 时直接跳过（<c>False</c>）。
        /// <para>玩家派系的小人本来就有 ideo；这里只在为空时兜底挂到玩家主文化，
        /// 然后把戒律数与食物类戒律打出来 —— 验收要的"文化存在戒律"是可查证的。</para>
        /// </summary>
        private static bool EnsureIdeo(Pawn pawn)
        {
            if (!ModsConfig.IdeologyActive) return false;

            if (pawn.Ideo == null)
            {
                Ideo ideo = Faction.OfPlayer?.ideos?.PrimaryIdeo;
                if (ideo == null)
                {
                    List<Ideo> all = Find.IdeoManager?.IdeosListForReading;
                    if (all != null)
                    {
                        for (int i = 0; i < all.Count; i++)
                        {
                            if (all[i] != null && all[i].PreceptsListForReading.Count > 0) { ideo = all[i]; break; }
                        }
                    }
                }
                if (ideo != null) pawn.ideo.SetIdeo(ideo);
            }
            return pawn.Ideo != null;
        }

        /// <summary>
        /// 授一个**带食物要求**的帝国头衔，并保证 <c>RoyalTitle.conceited</c> 为 true。
        ///
        /// <para>头衔选 <c>Knight</c>（<c>XML\Royalty\Defs\RoyalTitles\RoyalTitles_Empire.xml:181</c>）：
        /// 它是帝国里**最低的**带 <c>foodRequirement</c> 的头衔（<c>minQuality = MealSimple</c>），
        /// 副作用（居所要求 / 禁用工作）最小；再往下 Freeholder / Yeoman / Acolyte 都没有食物要求，
        /// 测不出这个口径。</para>
        ///
        /// <para><c>conceited</c> 是 <c>RoyalTitle</c> 的 public 字段（<c>RimWorld\RoyalTitle.cs:18</c>），
        /// 在 <c>SetTitle</c> 建对象时按特质算；这里在授完之后**再兜底强制置 true**，
        /// 免得"特质没生效/是任务寄居者"导致测试假绿。</para>
        /// </summary>
        private static RoyalTitle GrantConceitedTitle(Pawn pawn)
        {
            if (!ModsConfig.RoyaltyActive)
            {
                Log.Warning("[DS V-B0-4] 没有 Royalty DLC ⇒ 没有头衔，本场景只能测文化戒律那半边。");
                return null;
            }
            if (pawn.royalty == null || Faction.OfEmpire == null || RoyalTitleDefOf.Knight == null) return null;

            try
            {
                pawn.royalty.SetTitle(Faction.OfEmpire, RoyalTitleDefOf.Knight, grantRewards: false, sendLetter: false);
            }
            catch (System.Exception e)
            {
                Log.Error("[DS V-B0-4] 授头衔失败：" + e);
                return null;
            }

            RoyalTitle title = pawn.royalty.MostSeniorTitle;
            if (title != null && !title.conceited)
            {
                title.conceited = true;
                Log.Warning("[DS V-B0-4] 头衔本来不是 conceited（特质没被识别），已强制置 true 以保证测到食物要求。");
            }
            return title;
        }

        /// <summary>压饥饿度（见 <see cref="HungryLevelPercentage"/> 的注释：饿但不饿死）。</summary>
        private static void MakeHungry(Pawn pawn)
        {
            Need_Food food = pawn.needs?.food;
            if (food == null) return;
            food.CurLevelPercentage = HungryLevelPercentage;
        }

        // ===================================================================
        // 核心与食物
        // ===================================================================

        /// <summary>
        /// 离 <paramref name="cell"/> 最近的核心。
        /// <para>优先用 <c>CoreFinder.AllUsableCores(map)</c>（口径 = 能真的被抽取到），
        /// 找不到时退回枚举本图所有核心 —— 这样"没通电"也能被点名，而不是让测试者以为没造核心。</para>
        /// </summary>
        private static Building_StorageCore NearestCore(Map map, IntVec3 cell, out int count)
        {
            List<Building_StorageCore> usable = CoreFinder.AllUsableCores(map);
            count = usable.Count;
            Building_StorageCore best = Nearest(usable, cell);
            if (best != null) return best;

            var tmp = new List<Building_StorageCore>();
            foreach (Building_StorageCore c in map.listerBuildings.AllBuildingsColonistOfClass<Building_StorageCore>())
            {
                if (c != null && !c.Destroyed && c.Spawned) tmp.Add(c);
            }
            count = tmp.Count;
            return Nearest(tmp, cell);
        }

        private static Building_StorageCore Nearest(List<Building_StorageCore> cores, IntVec3 cell)
        {
            Building_StorageCore best = null;
            float bestDist = float.MaxValue;
            for (int i = 0; i < cores.Count; i++)
            {
                Building_StorageCore c = cores[i];
                if (c == null || c.Destroyed || !c.Spawned) continue;
                float d = c.Position.DistanceTo(cell);
                if (d < bestDist) { bestDist = d; best = c; }
            }
            return best;
        }

        private static Pawn NearestTitledColonist(Map map, IntVec3 cell)
        {
            Pawn best = null;
            float bestDist = float.MaxValue;
            IReadOnlyList<Pawn> pawns = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn p = pawns[i];
                if (p == null || p.royalty == null || p.royalty.MostSeniorTitle == null) continue;
                float d = p.Position.DistanceTo(cell);
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best;
        }

        /// <summary>清空核心内容物（dev 工具，直接销毁；返回件数）。</summary>
        private static int ClearCore(Building_StorageCore core)
        {
            if (core?.innerContainer == null) return 0;
            List<Thing> list = new List<Thing>(core.innerContainer);
            int n = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Thing t = list[i];
                if (t == null || t.Destroyed) continue;
                core.innerContainer.Remove(t);
                t.Destroy(DestroyMode.Vanish);
                n++;
            }
            return n;
        }

        /// <summary>把 <paramref name="def"/> 种食物塞进核心（无视筛选，见 <c>TryStoreIgnoringFilter</c>）。</summary>
        private static int Stock(Building_StorageCore core, ThingDef def, int count)
        {
            if (core == null || def == null) return 0;
            Thing t;
            try
            {
                t = ThingMaker.MakeThing(def);
            }
            catch (System.Exception e)
            {
                Log.Error("[DS V-B0-4] MakeThing 失败：" + def.defName + "：" + e);
                return 0;
            }
            if (t == null) return 0;
            t.stackCount = Mathf.Clamp(count, 1, def.stackLimit);
            int stored = core.TryStoreIgnoringFilter(t, t.stackCount);
            if (stored <= 0 && !t.Destroyed) t.Destroy(DestroyMode.Vanish);
            return stored;
        }

        /// <summary>
        /// 在候选里找第一件 <c>WillEat == wantWillEat</c> 的食物（口径就是补丁的抽取判据）。
        /// </summary>
        private static ThingDef FindFood(Pawn pawn, string[] candidates, bool wantWillEat)
        {
            for (int i = 0; i < candidates.Length; i++)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(candidates[i]);
                if (def?.ingestible == null) continue;
                if (!def.IsNutritionGivingIngestible) continue;
                if (!pawn.RaceProps.Eats(def.ingestible.foodType)) continue;
                bool will = pawn.WillEat(def, pawn, careIfNotAcceptableForTitle: true);
                if (will == wantWillEat) return def;
            }
            return null;
        }

        // ===================================================================
        // 打印
        // ===================================================================

        private static void AppendPawnState(Pawn p, RoyalTitle title)
        {
            TraitSet traits = p.story?.traits;
            SB.AppendLine("  阵营=" + (p.Faction == null ? "无" : p.Faction.Name)
                          + "  IsColonist=" + p.IsColonist
                          + "  IsQuestLodger=" + p.IsQuestLodger()
                          + "  Ascetic=" + (traits != null && traits.HasTrait(TraitDefOf.Ascetic))
                          + "  IsPawnConceited=" + RoyalTitleUtility.IsPawnConceited(p));

            if (traits != null)
            {
                SB.Append("  特质：");
                List<Trait> all = traits.allTraits;
                for (int i = 0; i < all.Count; i++) SB.Append(all[i].def.defName + " ");
                SB.AppendLine();
            }

            if (title == null)
            {
                SB.AppendLine("  头衔：无（Royalty 未启用，或授勋失败）");
            }
            else
            {
                RoyalTitleFoodRequirement req = title.def.foodRequirement;
                SB.AppendLine("  头衔：" + title.def.defName + "（" + (title.faction == null ? "?" : title.faction.Name) + "）"
                              + "  conceited=" + title.conceited);
                if (!req.Defined)
                {
                    SB.AppendLine("    ✗ 这个头衔**没有**食物要求（foodRequirement.Defined=false）⇒ 测不到头衔口径，换 Knight 及以上");
                }
                else
                {
                    SB.Append("    食物要求：minQuality=" + req.minQuality
                              + "  allowedTypes=" + req.allowedTypes
                              + "  allowedDefs=");
                    if (req.allowedDefs == null || req.allowedDefs.Count == 0) SB.Append("（无）");
                    else for (int i = 0; i < req.allowedDefs.Count; i++) SB.Append(req.allowedDefs[i].defName + " ");
                    SB.AppendLine();
                }
            }

            Need_Food food = p.needs?.food;
            if (food == null)
            {
                SB.AppendLine("  饥饿：无 Need_Food（不是能吃的生物）");
            }
            else
            {
                SB.AppendLine("  饥饿：" + food.CurLevelPercentage.ToStringPercent()
                              + "  分类=" + food.CurCategory
                              + "  Starving=" + food.Starving
                              + (food.Starving ? "  ⚠️ 饿死状态会被 allowIfStarving 放行，测不到头衔口径" : ""));
            }

            if (!ModsConfig.IdeologyActive)
            {
                SB.AppendLine("  文化：无 Ideology DLC");
            }
            else if (p.Ideo == null)
            {
                SB.AppendLine("  文化：ideo 为空（无戒律可查）");
            }
            else
            {
                List<Precept> precepts = p.Ideo.PreceptsListForReading;
                SB.AppendLine("  文化：" + p.Ideo.name + "  戒律 " + precepts.Count + " 条"
                              + "  确定性=" + p.ideo.Certainty.ToStringPercent()
                              + "  尊崇动物=" + p.Ideo.VeneratedAnimals.Count + " 种");
                for (int i = 0; i < precepts.Count; i++)
                {
                    Precept pr = precepts[i];
                    string defName = pr.def?.defName ?? "?";
                    // 只点名"跟吃有关"的戒律，避免刷屏；其余只报数量
                    if (defName.StartsWith("AnimalVenerated")
                        || defName.StartsWith("MeatEating")
                        || defName.StartsWith("Cannibalism")
                        || defName.StartsWith("NutrientPaste")
                        || defName.IndexOf("Food", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        SB.AppendLine("    · " + defName + " —— " + (pr.def?.LabelCap ?? "?"));
                    }
                }
            }
        }

        /// <summary>
        /// 口径全表：每个候选都**真的调用**原版三个判据（头衔 / 戒律 / 合吃），
        /// 与 <c>Patch_FeedFromStorage</c> 的抽取谓词同源。
        /// </summary>
        private static void AppendFoodTable(Pawn p)
        {
            SB.AppendLine("  defName | 营养/件 | preferability | foodType | 头衔拒 | 戒律拒 | WillEat");
            AppendTableRow(p, new[] { "RawPotato", "RawRice", "RawCorn", "MealSurvivalPack" });
            SB.AppendLine("  --- 合规候选 ---");
            AppendTableRow(p, new[] { "MealSimple", "Pemmican", "MealFine", "MealLavish", "RawBerries", "MealNutrientPaste" });
        }

        private static void AppendTableRow(Pawn p, string[] defNames)
        {
            for (int i = 0; i < defNames.Length; i++)
            {
                ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(defNames[i]);
                if (def?.ingestible == null)
                {
                    SB.AppendLine("  " + defNames[i] + " | （本档没这个 def，跳过）");
                    continue;
                }
                bool titleReject = FoodUtility.InappropriateForTitle(def, p, allowIfStarving: false);
                bool veneratedReject = p.Ideo != null && FoodUtility.IsVeneratedAnimalMeatOrCorpse(def, p);
                bool willEat = p.WillEat(def, p, careIfNotAcceptableForTitle: true);
                float nutrition = def.GetStatValueAbstract(StatDefOf.Nutrition);
                SB.AppendLine("  " + def.defName
                              + " | " + nutrition.ToString("0.###")
                              + " | " + def.ingestible.preferability
                              + " | " + def.ingestible.foodType
                              + " | " + (titleReject ? "是" : "否")
                              + " | " + (veneratedReject ? "是" : "否")
                              + " | " + (willEat ? "**吃**" : "**不吃**"));
            }
        }

        /// <summary>全图核心内容物（"食物只在核心"的现场证据）。</summary>
        private static void AppendCoreContents(Pawn p)
        {
            Map map = p.Map;
            List<Building_StorageCore> cores = CoreFinder.AllUsableCores(map);
            SB.AppendLine("  —— 本图可用核心 " + cores.Count + " 个 ——");
            for (int i = 0; i < cores.Count; i++)
            {
                Building_StorageCore core = cores[i];
                SB.AppendLine("  核心 " + core.Position + " 距" + p.LabelShort + "="
                              + core.Position.DistanceTo(p.Position).ToString("0.0")
                              + "  内容 " + core.innerContainer.Count + " 堆：");
                for (int j = 0; j < core.innerContainer.Count; j++)
                {
                    Thing t = core.innerContainer[j];
                    if (t == null) continue;
                    bool willEat = t.def != null && p.WillEat(t.def, p, careIfNotAcceptableForTitle: true);
                    SB.AppendLine("    · " + t.def.defName + " × " + t.stackCount + "  WillEat=" + willEat);
                }
            }
        }
    }
}
