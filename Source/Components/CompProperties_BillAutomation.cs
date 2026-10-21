using System.Collections.Generic;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// <b>制作代理</b>的 comp 属性。
    ///
    /// <para><b>为什么不复用 <see cref="CompProperties_DigitalWorker"/></b>：那一套围绕
    /// <c>WorkGiver</c> 扫描设计（<c>workTypes</c> + 候选集 + 认领表）。而 bill 自动化的活来自
    /// **工作台上的 <c>BillStack</c>**，跟 WorkTypeDef 无关（也因此不需要给假工人开工作类型、
    /// 玩家也不需要为机器配工种优先级）。共用的只有"假 pawn 工厂"与表现件。</para>
    ///
    /// <para><b>一台工作台 = 一个槽位 = 一个虚拟工匠</b>：所以"同一个 bill 在三个台子都有 ⇒ 并发 3"
    /// 是自然结果，不需要额外记并发数。</para>
    /// </summary>
    public class CompProperties_BillAutomation : CompProperties
    {
        /// <summary>资质（用户拍板：5 / 10 / 15 / 20）。进 <c>recipe.skillRequirements</c> 门槛与产物品质，不进速度倍率。</summary>
        public int skillLevel = 5;

        /// <summary>
        /// 速度倍率（用户拍板：0.8 / 1 / 1.5 / 2）。
        ///
        /// <para>乘在**原版速度公式**上（<c>Toils_Recipe.cs:109</c>）：
        /// <c>rate = (recipe.workSpeedStat==null ? 1 : 假工人.GetStatValue(...)) × 台子.workTableSpeedStat × 本值</c>。
        /// 注意 <c>workSpeedStat</c> 本身吃技能（做饭类 <c>CookSpeed</c> 就是技能驱动的），
        /// 所以高等级的**实际**差距会大于倍率比 —— 这是用户拍板的"走原版就好"。</para>
        /// </summary>
        public float workSpeedMult = 0.8f;

        /// <summary>
        /// 扫描范围：**以建筑为中心的 13×13 方形**（用户拍板"方形"）⇒ 半边长 6。
        /// 用 <c>CellRect</c> 而不是 <c>GenRadial</c>（后者是圆的）。
        /// </summary>
        public int scanRadius = 6;

        /// <summary>自身固定耗电（W）：用户拍板 300W，**等级不增耗电**。</summary>
        public float basePowerWatts = 300f;

        /// <summary>范围内台子里**没有电力组件**的（手工点 / 手工缝纫台）折算的固定耗电：用户拍板 100W。</summary>
        public float benchWithoutPowerWatts = 100f;

        /// <summary>槽位硬上限（一台 = 一个槽位）。防"满图台子"时并行爆掉。</summary>
        public int maxSlots = 64;

        /// <summary>台子扫描间隔（tick）。台子集合极少变，默认 60。</summary>
        public int scanIntervalTicks = 60;

        /// <summary>
        /// 每 tick 最多"取活"几次。
        ///
        /// <para>每次取活都会走一遍原版选料（<c>WorkGiver_DoBill.JobOnThing</c>）：核心里有料时
        /// 只是扫一遍容器内容物，**没料时才会做全图区域遍历**。原版自己会给失败的 bill 写
        /// 500~600 tick 负缓存（<c>WorkGiver_DoBill.cs:265</c>），所以稳态下这几乎不进热路径；
        /// 这个配额只是兜"读档后满场槽位同时取活"那一下。</para>
        /// </summary>
        public int maxProbesPerTick = 2;

        /// <summary>取活失败后的重试间隔（tick）。原版的 500~600 负缓存只在"选料失败"时写。</summary>
        public int noWorkRetryTicks = 30;

        /// <summary>
        /// 最多给几件在产的活画"手 + 黄条"（纯表现上限，机制不受影响）。
        /// <para>2026-10-21 用户反馈"只显示 6 个手" ⇒ 默认 **0 = 不限制**（有几条线画几只）。
        /// 想要老行为就在 XML 里给个正整数。</para>
        /// </summary>
        public int maxVisualSlots = 0;

        /// <summary>
        /// 左上角完成提示的最小间隔（tick）。0 = 每 tick 都提示。
        /// 默认 30（0.5 秒）—— 超频 ×9 时产物是按秒刷的，不做聚合会把消息栏刷爆。
        /// </summary>
        public int messageIntervalTicks = 30;

        public CompProperties_BillAutomation()
        {
            compClass = typeof(CompBillAutomation);
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string e in base.ConfigErrors(parentDef))
            {
                yield return e;
            }
            if (skillLevel < 0 || skillLevel > 20)
            {
                yield return parentDef.defName + "：skillLevel 应在 0~20（收到 " + skillLevel + "）。";
            }
            if (workSpeedMult <= 0f)
            {
                yield return parentDef.defName + "：workSpeedMult 必须 > 0（收到 " + workSpeedMult + "）。";
            }
            if (scanRadius < 0)
            {
                yield return parentDef.defName + "：scanRadius 不能为负（收到 " + scanRadius + "）。";
            }
            if (maxSlots < 1)
            {
                yield return parentDef.defName + "：maxSlots 至少为 1（收到 " + maxSlots + "）。";
            }
            if (maxProbesPerTick < 1)
            {
                yield return parentDef.defName + "：maxProbesPerTick 至少为 1（收到 " + maxProbesPerTick + "）。";
            }
        }
    }
}
