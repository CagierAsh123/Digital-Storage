using System.Collections.Generic;
using DigitalStorage.Backpack;
using RimWorld;
using Verse;

namespace DigitalStorage.Services
{
    /// <summary>
    /// 4.0 更新信封：每个存档（含新开档）弹一次，沿用 3.0 的形态 ——
    /// <c>GameComponent.FinalizeInit</c> + 一个被 Scribe 的布尔标记。
    ///
    /// <para><b>为什么沿用 <c>DigitalStorage.Services.DigitalStorageGameComponent</c> 这个类名</b>：
    /// 3.0 的存档里有这个组件的条目（旧字段 <c>globalCores</c> / <c>shown30Letter</c>）。
    /// 同名重建 ⇒ 读档时组件能解析、旧字段被安静忽略（RimWorld 不报"未知组件"），
    /// 而且 <c>shown30Letter</c> 与这里的 <c>shown40Letter</c> 是**不同的 Scribe 键**
    /// ⇒ 从 3.0 升上来的玩家**照样会看到这封信**（这正是我们要的）。</para>
    ///
    /// <para>信本身用 <c>LetterDefOf.NeutralEvent</c>，不需要自定义 LetterDef。</para>
    ///
    /// <para><b>第二个职责：背包对账</b>（见 <see cref="GameComponentTick"/>）。</para>
    /// </summary>
    public class DigitalStorageGameComponent : GameComponent
    {
        private bool shown40Letter;

        /// <summary>对账周期（tick）。250 ≈ 4 秒。</summary>
        private const int ReconcileInterval = 250;

        public DigitalStorageGameComponent(Game game) { }

        public override void GameComponentTick()
        {
            base.GameComponentTick();

            // 「谁该有数字存储背包」的**周期性对账**。
            //
            // 为什么不能只靠事件钩子：① 原版招募那一步的顺序很坑 —— RecruitUtility.Recruit 先
            // guest.SetGuestStatus(null) 再 pawn.SetFaction(player)（RecruitUtility.cs:24-27），
            // 挂在 SetGuestStatus 上的钩子跑在**换阵营之前**，那一瞬间看到的是旧阵营 ⇒ 判定"不该有"
            // （2026-10-03 用户反馈"招募囚犯后不加背包"就是它）；② 任何"换了阵营却不走 Pawn.SetFaction"
            // 的第三方路径都会漏。对账一次只花"pawn 数 × 一次 hediff 查表"，4 秒一次，最便宜的自愈。
            if (Find.TickManager == null || Find.TickManager.TicksGame % ReconcileInterval != 0) return;

            List<Map> maps = Find.Maps;
            if (maps == null) return;

            for (int i = 0; i < maps.Count; i++)
            {
                Map map = maps[i];
                if (map == null || map.mapPawns == null) continue;

                IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
                for (int j = 0; j < pawns.Count; j++)
                {
                    // Sync 自带判据与 try/catch：不归我们管的 pawn 一次字段判空就返回。
                    BackpackImplant.Sync(pawns[j]);
                }
            }
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();

            // B7 / 审计 2026-10-17：跨档静态表按 thingIDNumber 记，换档必须清
            //（否则上一档的编号会命中新档里无关的东西）。四张表都是"运行时加速器"，
            // 清空只丢一点缓存，不影响任何已存状态。
            Components.CompAutoIngest.ClearSessionCaches();
            AI.BillBenchClaims.ClearAll();
            AI.CraftIngredientClaims.Clear();

            if (!shown40Letter)
            {
                shown40Letter = true;
                Find.LetterStack.ReceiveLetter(
                    "DigitalStorage_Letter40_Label".Translate(),
                    "DigitalStorage_Letter40_Text".Translate(),
                    LetterDefOf.NeutralEvent);
            }

            // 与 Pick Up And Haul 不再互斥（2026-10-18 改为真兼容，结论见 PickUpAndHaulCompat 类注释），
            // 那封「每次进档都弹」的红信已删除 —— 兼容改动本身不需要玩家做任何事。
        }

        public override void ExposeData()
        {
            base.ExposeData();
            // 键名与字段名保持一致：Scribe 的键一经写入就固定，改名就会"读不到 ⇒ 每档重弹"
            // （3.0 那个 F1 注释记的就是这个坑）。
            Scribe_Values.Look(ref shown40Letter, "shown40Letter", false);
        }
    }
}
