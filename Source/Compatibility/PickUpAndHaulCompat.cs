using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace DigitalStorage.Compatibility
{
    /// <summary>
    /// <b>与 Pick Up And Haul（PUAH）的兼容层</b>。
    ///
    /// <para><b>立场变更</b>：2026-10-12 曾拍板「声明互斥、不做兼容」，理由是「入库是瞬移，
    /// 与 PUAH 的批量拾取差了一个搬运作业」。2026-10-18 用户要求改成真兼容 —— 读完对方
    /// 1.6 源码（工坊 `1279012058`，反编译见 <c>_SourceCode\Pick Up And Haul\1.6\Source</c>）后，
    /// 那个理由其实站不住，两者可以共存：</para>
    ///
    /// <list type="number">
    /// <item><b>PUAH 看不见核心里的东西</b>：它只吃
    /// <c>WorkGiver_HaulToInventory.PotentialWorkThingsGlobal</c> 的 <c>listerHaulables</c> 表，
    /// 而每件候选都要过 <c>OkThingToHaul</c> 的 <c>t.Spawned</c>（<c>WorkGiver_HaulToInventory.cs:143</c>）。
    /// 本 mod 的 <c>Building_StorageCore</c> 虽是 <c>IHaulSource</c>（内容物会进那张表），
    /// 但内容物 <c>Spawned == false</c> ⇒ PUAH 一件都拿不到，不会出现「两边同时搬同一件」。</item>
    /// <item><b>它不会搬坏核心</b>：PUAH 自己的目的地搜索只读
    /// <c>AllHaulDestinationsListInPriorityOrder</c> + <c>Accepts</c> + <c>GetStoreSettings().Priority</c>
    /// （<c>:531-572</c>），与《容器型目的地-规则必须落在插入漏斗上》那份结论一致 ——
    /// 真正的入库动作仍然是 <c>Toils_Haul.DepositHauledThingInContainer</c> 里的
    /// <c>TryTransferToContainer</c> ⇒ 走到本 mod 的 <c>CoreStorageContainer.TryAdd</c>，
    /// 筛选 / 断电 / 入库开关三道规则照样生效。</item>
    /// <item><b>唯一的真实摩擦是"瞬移抢在作业之前"</b>：
    /// <c>CompAutoIngest.RejectReason</c> 里的 <c>Reject.Reserved</c> 只认
    /// <c>ReservationManager.IsReserved</c>，而 PUAH 的组批作业是
    /// 「先 <c>JobMakePreToilReservations</c> 一次性预订整条 <c>targetQueueA</c>」⇒ 预订前那一瞬、
    /// 以及预订失败的尾部条目，仍然可能被我们瞬移走，让那趟活白跑。
    /// PUAH 在场时自动把 <c>Reject.JobTarget</c>（是某个 pawn 的作业目标）那道闸门**强制打开**，
    /// 见 <see cref="YieldToJobTargets"/>。</item>
    /// </list>
    ///
    /// <para><b>已知残留（有界，不修）</b>：PUAH 组批时对每个新 def 都重新问一次
    /// <c>ThingOwner.GetCountCanAccept</c> 且不扣已分配的名额 ⇒ 只剩一个空堆位时可能超额组批，
    /// 入库时尾部失败、物品留在小人手里（<c>PawnUnloadChecker</c> 之后会再找目的地，不会销毁）。
    /// 这是对方算法的固有近似，只表现为「多跑一趟」。</para>
    ///
    /// <para><b>为什么不做 Harmony 补丁</b>：上面三条结论都不需要改对方代码 ——
    /// 我们只用「一道更严的自动收纳闸门」换掉过去那封不兼容红信。
    /// 不往 PUAH 程序集里挂补丁 = 对方更新换代时不会因为我们而炸。</para>
    /// </summary>
    internal static class PickUpAndHaulCompat
    {
        /// <summary>PUAH 本体（工坊 1279012058）。</summary>
        private const string PackageId = "Mehni.PickUpAndHaul";

        /// <summary>中文分支（工坊 3522897816）—— 只查本体 packageId 会漏掉它。</summary>
        private const string PackageIdCn = "day29zh.PickUpAndHaul";

        /// <summary>
        /// PUAH 的一个类型名（它的 <c>CompHauledToInventory</c>，ThingComp，代码里写死的类名）。
        ///
        /// <para><b>为什么不能只查 <c>ModsConfig</c></b>：fork / 汉化分支 / 手装副本 / 改名的
        /// packageId 各不相同，而"装配体里到底有没有 PUAH 的补丁代码"才是我们要问的问题 ——
        /// 类型名比 packageId 稳。两条路都走：先 mod 列表（覆盖已知 id），再类型表（覆盖未知的改名分支）。</para>
        /// </summary>
        private const string ProbeType = "PickUpAndHaul.CompHauledToInventory";

        private static bool probed;
        private static bool active;

        /// <summary>PUAH 在不在（结果缓存：mod 列表与类型表在整个进程里不会变）。</summary>
        public static bool IsActive
        {
            get
            {
                if (probed) return active;
                probed = true;
                try
                {
                    if (ModsConfig.IsActive(PackageId) || ModsConfig.IsActive(PackageIdCn)) active = true;
                    else active = AccessTools.TypeByName(ProbeType) != null;
                }
                catch (Exception e)
                {
                    active = false;
                    Log.ErrorOnce("[DigitalStorage] 检测 Pick Up And Haul 时出错（按未安装处理）：" + e, 0x44534301);
                }
                return active;
            }
        }

        /// <summary>
        /// 自动收纳要不要<u>强制</u>让位给「某个 pawn 作业正指着它」的东西。
        ///
        /// <para>玩家设置 <c>autoIngestYieldToJobs</c> 打开时本来就生效；
        /// PUAH 在场时无论设置如何都生效 —— 它一趟要搬七八堆，被瞬移走一堆就白跑一趟。</para>
        /// </summary>
        public static bool YieldToJobTargets =>
            DigitalStorage.Settings.DigitalStorageSettings.autoIngestYieldToJobs
            || IsActive;

        /// <summary>
        /// 启动时写一行日志（每进程一次）。放在 <c>HarmonyInit</c> 的静态构造里而不是读档时：
        /// 反馈里附的第一个文件永远是 <c>Player.log</c>，那时玩家可能还没进档。
        /// </summary>
        public static void LogOnce()
        {
            if (!IsActive) return;
            Log.Message("[DigitalStorage] 检测到 Pick Up And Haul：兼容已启用（批量拾取照常，"
                + "自动收纳会对'被作业指着的东西'让位；核心的入库规则仍在容器插入漏斗上执行）");
        }
    }
}
