using RimWorld;
using Verse;

namespace DigitalStorage.Components
{
    /// <summary>
    /// 终端芯片使用效果 - 直接使用添加Hediff
    /// </summary>
    public class CompUseEffect_TerminalImplant : CompUseEffect
    {
        public override void DoEffect(Pawn user)
        {
            base.DoEffect(user);
            Hediff_TerminalImplant.AddTerminalImplant(user);
            Messages.Message($"{user.LabelShort} 已植入终端芯片，现在可以远程访问数字存储系统", user, MessageTypeDefOf.PositiveEvent);
        }

        public override AcceptanceReport CanBeUsedBy(Pawn p)
        {
            if (p == null || !p.RaceProps.Humanlike)
                return "只有人类可以使用";

            if (Hediff_TerminalImplant.HasTerminalImplant(p))
                return "已安装终端芯片";

            return true;
        }

        public override TaggedString ConfirmMessage(Pawn p)
        {
            return $"确定要为 {p.LabelShort} 植入终端芯片吗？\n\n植入后可以远程访问数字存储系统，物品将直接传送到脚下。";
        }
    }

    public class CompProperties_UseEffect_TerminalImplant : CompProperties_UseEffect
    {
        public CompProperties_UseEffect_TerminalImplant()
        {
            this.compClass = typeof(CompUseEffect_TerminalImplant);
        }
    }
}
