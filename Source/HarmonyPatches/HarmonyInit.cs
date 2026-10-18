using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    [StaticConstructorOnStartup]
    public static class HarmonyInit
    {
        static HarmonyInit()
        {
            var harmony = new Harmony("DigitalStorage.HarmonyPatches");
            PatchAllIsolated(harmony);
            // 带 out/ref 参数的方法写不进 [HarmonyPatch] 特性（特性实参必须是常量/typeof），只能手挂
            ManualInstallAll(harmony);
            // 第三方定向兼容（可选 mod）：必须在 PatchAll 之后单独装，失败也不能影响本 mod 的补丁
            Compatibility.PhinixCompatPatch.Install(harmony);
            // Pick Up And Haul：现在是**兼容**不是互斥（结论见 PickUpAndHaulCompat 类注释）。
            // 加载顺序有讲究：必须在 PatchAll 之前探明它是否在场吗？——不需要，这里只是打一行日志；
            // 真正决定自动收纳闸门的是运行期的 PickUpAndHaulCompat.YieldToJobTargets。
            Compatibility.PickUpAndHaulCompat.LogOnce();
            // Log.Message 在游戏内日志窗口不显示（只有 Player.log 有），诊断一律用 Warning。
            // 带上 DLL 的最后写入时间：实测对账时最怕"跑的是哪一版"，以后一眼可辨。
            string buildTime;
            try
            {
                buildTime = System.IO.File.GetLastWriteTime(typeof(HarmonyInit).Assembly.Location)
                    .ToString("yyyy-MM-dd HH:mm");
            }
            catch
            {
                buildTime = "?";
            }
            Log.Warning("[DigitalStorage 4.0] Harmony ready. 构建=" + buildTime + PatchSummary);
            // 第三方兼容挂点数量（仅开发者模式；Phinix/红包未安装或改名时为 0）
            if (Prefs.DevMode)
                Log.Warning("[DigitalStorage] 第三方兼容挂点=" + Compatibility.PhinixCompatPatch.InstalledCount);
        }

        internal static string PatchSummary = "";

        /// <summary>
        /// <b>手挂补丁</b>：目标方法带 <c>out</c>/<c>ref</c> 参数时，
        /// <c>[HarmonyPatch(typeof(X), "M", new[]{ … typeof(T).MakeByRefType() … })]</c>
        /// 编译不过（特性实参必须是常量表达式 / <c>typeof</c> / 数组创建），
        /// 只能用 <c>AccessTools.Method</c> 在代码里定位后手挂。
        /// 同样逐个 try/catch —— 手挂失败也不能影响其余补丁。
        /// </summary>
        private static void ManualInstallAll(Harmony harmony)
        {
            int ok = 0;
            var failed = new List<string>();

            try
            {
                Patch_GenPlace_DropRedirect.Install(harmony);
                ok++;
            }
            catch (Exception ex)
            {
                failed.Add("Patch_GenPlace_DropRedirect");
                Log.Error("[DigitalStorage] 手挂补丁失败（已跳过）：Patch_GenPlace_DropRedirect :: " + ex);
            }

            PatchSummary += " 手挂=" + ok;
            if (failed.Count > 0)
            {
                PatchSummary += " 手挂失败=" + failed.Count + " [" + string.Join(",", failed.ToArray()) + "]";
            }
        }

        /// <summary>
        /// <b>逐个补丁类隔离挂载</b>，而不是 <c>harmony.PatchAll()</c>。
        ///
        /// <para>为什么必须这样：<c>PatchAll()</c> 是**一个循环**，任何一次挂载抛异常
        /// （参数名写错 / 目标方法有重载导致 <c>AmbiguousMatchException</c> /
        /// 类里那个约定名为 <c>HarmonyInit</c> 的静态初始化器抛异常），
        /// <b>循环后面的补丁类全部静默不挂</b> —— 游戏照常启动，只是功能一个个消失，
        /// 而日志里只有一条被埋掉的报错。本 mod 2026-10-01 已经因此丢过整整一批补丁
        /// （<c>Toils_Goto.GotoThing</c> 有两个重载 ⇒ AmbiguousMatchException ⇒
        /// 之后所有补丁静默失效）。</para>
        ///
        /// <para>现在每个补丁类各自 try/catch：坏的那一个被点名报出来，其余照常生效。</para>
        /// </summary>
        private static void PatchAllIsolated(Harmony harmony)
        {
            Type[] types;
            try
            {
                types = typeof(HarmonyInit).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
                Log.Error("[DigitalStorage] 程序集类型枚举部分失败，跳过为 null 的项：" + e.Message);
            }

            int ok = 0;
            var failed = new List<string>();
            var okNames = Prefs.DevMode ? new List<string>() : null;

            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i];
                if (type == null) continue;
                if (type.GetCustomAttributes(typeof(HarmonyPatch), true).Length == 0) continue;

                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    ok++;
                    okNames?.Add(type.Name);
                }
                catch (Exception ex)
                {
                    failed.Add(type.Name);
                    Log.Error("[DigitalStorage] 补丁类挂载失败（已跳过，其余继续）：" + type.FullName + " :: " + ex);
                }
            }

            PatchSummary = failed.Count == 0
                ? " 补丁类=" + ok
                : " 补丁类=" + ok + " 失败=" + failed.Count + " [" + string.Join(",", failed.ToArray()) + "]";

            // 【为什么值得多打这一行】2026-10-02 踩过：新补丁文件没写进 csproj 的 <Compile>，
            // 构建成功、DLL 里连类型名都没有 ⇒ 那个补丁**根本没进程序集**，
            // 而 `CreateClassProcessor` 也不会有任何报错（它压根没被枚举到），
            // 计数只会静静地少一个 —— 光看 count 是发现不了的。所以 dev 模式把名字全列出来。
            if (okNames != null)
                Log.Warning("[DigitalStorage] 已挂补丁类：" + string.Join(",", okNames.ToArray()));
        }
    }
}
