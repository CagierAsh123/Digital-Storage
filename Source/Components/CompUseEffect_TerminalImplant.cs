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
            Messages.Message("DS_ImplantSuccess".Translate(user.LabelShort), user, MessageTypeDefOf.PositiveEvent);
        }

        public override AcceptanceReport CanBeUsedBy(Pawn p)
        {
            if (p == null || !p.RaceProps.Humanlike)
                return "DS_OnlyHumans".Translate();

            if (Hediff_TerminalImplant.HasTerminalImplant(p))
                return "DS_AlreadyImplanted".Translate();

            return true;
        }

        public override TaggedString ConfirmMessage(Pawn p)
        {
            return "DS_ImplantConfirm".Translate(p.LabelShort);
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
