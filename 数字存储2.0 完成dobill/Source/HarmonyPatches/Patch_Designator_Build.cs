using System;
using System.Collections.Generic;
using System.Linq;
using DigitalStorage.Components;
using DigitalStorage.Services;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace DigitalStorage.HarmonyPatches
{
    /// <summary>
    /// 补丁：跨地图打开建筑菜单时，如果 Prefs.DevMode 为 true，跳过所有判定，直接允许放置蓝图
    /// </summary>
    [HarmonyPatch(typeof(Designator_Build), "ProcessInput")]
    public static class Patch_Designator_Build_ProcessInput
    {
        public static bool Prefix(Designator_Build __instance, Event ev)
        {
            // 开发者模式检测：如果 Prefs.DevMode 为 true，跳过所有判定，直接允许放置蓝图
            if (Prefs.DevMode)
            {
                // 开发者模式：跳过所有判定，允许放置蓝图
                // 使用原版逻辑，但临时启用 godMode 以绕过材料检查
                bool originalGodMode = DebugSettings.godMode;
                try
                {
                    DebugSettings.godMode = true;
                    // 继续执行原版逻辑
                    return true;
                }
                finally
                {
                    DebugSettings.godMode = originalGodMode;
                }
            }

            // 使用反射调用受保护的方法 CheckCanInteract
            bool canInteract = (bool)AccessTools.Method(typeof(Designator), "CheckCanInteract").Invoke(__instance, null);
            if (!canInteract)
            {
                return false;
            }
            
            ThingDef thingDef = __instance.PlacingDef as ThingDef;
            if (thingDef == null || !thingDef.MadeFromStuff)
            {
                // 不需要材料的建筑，使用原版逻辑
                return true;
            }
            
            // 获取全局游戏组件
            Game game = Current.Game;
            DigitalStorageGameComponent gameComp = (game != null) ? game.GetComponent<DigitalStorageGameComponent>() : null;
            if (gameComp == null || gameComp.GetAllCores().Count == 0)
            {
                // 没有核心，使用原版逻辑
                return true;
            }
            
            // 保存原始的 godMode 值
            bool originalGodMode2 = DebugSettings.godMode;
            
            try
            {
                // 临时启用上帝模式（允许选择虚拟存储中的材料）
                DebugSettings.godMode = true;
                
                // 创建材料选择菜单
                List<FloatMenuOption> list = new List<FloatMenuOption>();
                
                foreach (ThingDef thingDef2 in __instance.Map.resourceCounter.AllCountedAmounts.Keys.OrderByDescending(delegate(ThingDef d)
                {
                    StuffProperties stuffProps = d.stuffProps;
                    if (stuffProps == null)
                    {
                        return float.PositiveInfinity;
                    }
                    return stuffProps.commonality;
                }).ThenBy((ThingDef d) => d.BaseMarketValue))
                {
                    // 现在 godMode 为 true，所以这个检查会通过
                    if (thingDef2.IsStuff && thingDef2.stuffProps.CanMake(thingDef) && 
                        (DebugSettings.godMode || __instance.Map.listerThings.ThingsOfDef(thingDef2).Count > 0))
                    {
                        ThingDef localStuffDef = thingDef2;
                        string text;
                        
                        Precept_ThingStyle sourcePrecept = Traverse.Create(__instance).Field("sourcePrecept").GetValue<Precept_ThingStyle>();
                        if (sourcePrecept != null)
                        {
                            text = "ThingMadeOfStuffLabel".Translate(localStuffDef.LabelAsStuff, sourcePrecept.Label);
                        }
                        else
                        {
                            text = GenLabel.ThingLabel(__instance.PlacingDef, localStuffDef, 1);
                        }
                        text = text.CapitalizeFirst();
                        
                        list.Add(new FloatMenuOption(text, delegate
                        {
                            // 调用原版的 ProcessInput
                            AccessTools.Method(typeof(Designator), "ProcessInput").Invoke(__instance, new object[] { ev });
                            Find.DesignatorManager.Select(__instance);
                            Traverse.Create(__instance).Field("stuffDef").SetValue(localStuffDef);
                            Traverse.Create(__instance).Field("writeStuff").SetValue(true);
                        }, thingDef2, null, false, MenuOptionPriority.Default, null, null, 0f, null, null, true, 0, null)
                        {
                            tutorTag = "SelectStuff-" + thingDef.defName + "-" + localStuffDef.defName
                        });
                    }
                }
                
                if (list.Count == 0)
                {
                    Messages.Message("NoStuffsToBuildWith".Translate(), MessageTypeDefOf.RejectInput, false);
                    return false;
                }
                
                FloatMenu floatMenu = new FloatMenu(list);
                floatMenu.onCloseCallback = delegate
                {
                    Traverse.Create(__instance).Field("writeStuff").SetValue(true);
                };
                Find.WindowStack.Add(floatMenu);
                Find.DesignatorManager.Select(__instance);
                
                return false; // 跳过原版方法
            }
            finally
            {
                // 恢复原始的 godMode 值
                DebugSettings.godMode = originalGodMode2;
            }
        }
    }
}

