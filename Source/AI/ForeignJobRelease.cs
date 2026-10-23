using System;
using System.Collections.Generic;
using Verse;
using Verse.AI;

namespace DigitalStorage.AI
{
    /// <summary>
    /// 在代理**把一件东西弄没之前**，先把"别人还指着这件东西的 job"结束掉。
    ///
    /// <para><b>为什么必须做</b>（2026-10-23 实机：一条存档读完直接炸，两次 NullReferenceException）：</para>
    /// <para>代理是假 pawn：不预约、不排队（见 <see cref="DigitalWorkerFactory"/>），所以它会和殖民者
    /// 抢同一件东西 —— 框被代理建完（<c>Frame.CompleteConstruction</c> 内部 <c>Destroy()</c>）、
    /// 建筑被代理拆完（<c>Destroy(DestroyMode.Deconstruct)</c>）、蓝图被代理换成框
    /// （<c>Blueprint.TryReplaceWithSolidThing</c>）。如果此刻某殖民者的 <c>curJob</c> 还指着它：</para>
    /// <list type="number">
    /// <item><b>保存时</b>：<c>Verse\Scribe_TargetInfo.cs</c> 的 saving 分支见到 <c>value.Thing</c>
    /// 已销毁就 **整段不写** <c>&lt;targetA&gt;</c>（不报错、不留痕）；</item>
    /// <item><b>读档时</b>：<c>targetA</c> 静默退化成 <c>LocalTargetInfo.Invalid</c>，
    /// 于是 <c>JobDriver_ConstructFinishFrame.get_IsBuildingAttachment</c>（<c>RimWorld\JobDriver_ConstructFinishFrame.cs:16</c>）
    /// 里的 <c>Frame.def</c> 直接 NRE（<c>Frame</c> 为 null）；</item>
    /// <item><b>连带</b>：<c>JobDriver.SetupToils</c> 的 catch 会起一个 recovery 的
    /// <c>JobDriver_Wait</c>，而它首个 toil 的 initAction 是
    /// <c>base.Map.pawnDestinationReservationManager…</c> ⇒ 这一步也炸 ⇒ 日志
    /// "An error occurred while starting an error recover job… pawn 变 jobless"。</item>
    /// </list>
    ///
    /// <para><b>原版为什么没这个问题</b>：目标消失的那一 tick，它的 toil 就会自己
    /// <c>FailOnDespawnedNullOrForbidden</c> 结束 job；而"目标已销毁"与"下一 tick"之间
    /// 如果夹了一次存档，坏状态就落进了盘里。我们抢在殖民者前面删了东西，就必须由我们在
    /// 删的同一时刻把那条 job 收掉 —— <c>EndCurrentJob(JobCondition.Incompletable)</c> 正是
    /// 原版 <c>Toil.FailOnDespawnedOrNull</c> 用的条件，语义完全等价，只是提前不到一 tick。</para>
    ///
    /// <para><b>成本</b>：只在代理真的要走"删除/收尾"分支时跑一次，扫的是
    /// <c>MapPawns.AllPawnsSpawned</c>（一次线性扫 + 三条 target 比较）；失败整段吞掉，
    /// 绝不因为释放 job 而打断代理手上的活。</para>
    /// </summary>
    internal static class ForeignJobRelease
    {
        /// <summary>
        /// 结束所有"当前 job 正指着 <paramref name="thing"/> 的已生成 pawn"的 job。
        /// 必须在让 <paramref name="thing"/> 消失**之前**调用。
        /// </summary>
        /// <param name="thing">即将被 Destroy / 替换掉的目标。</param>
        /// <param name="self">本次操作的执行者（代理假 pawn），跳过它自己。</param>
        public static void BeforeVanishing(Thing thing, Pawn self)
        {
            if (thing == null || thing.Destroyed) return;

            Map map = thing.Map;
            if (map == null || map.mapPawns == null) return;   // 不在图上：没有 pawn 能指着它干活

            try
            {
                IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
                if (pawns == null) return;
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn p = pawns[i];
                    if (p == null || p == self || p.jobs == null) continue;
                    Job job = p.jobs.curJob;
                    if (job == null || !Targets(job, thing)) continue;
                    p.jobs.EndCurrentJob(JobCondition.Incompletable, startNewJob: false);
                }
            }
            catch (Exception e)
            {
                // 释放别人的 job 是"顺手做的卫生"，失败最多回退成旧行为（下一 tick 它自己失败）。
                Log.ErrorOnce("[DigitalStorage] 收掉目标上的他人 job 失败（不影响本次操作）：" + e, 0x5D531);
            }
        }

        private static bool Targets(Job job, Thing thing)
        {
            return Hits(job.targetA, thing) || Hits(job.targetB, thing) || Hits(job.targetC, thing);
        }

        private static bool Hits(LocalTargetInfo target, Thing thing)
        {
            return target.HasThing && target.Thing == thing;
        }
    }
}
