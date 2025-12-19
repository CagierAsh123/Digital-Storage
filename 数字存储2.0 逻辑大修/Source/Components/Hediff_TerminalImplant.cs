using System;
using Verse;

namespace DigitalStorage.Components
{
    public class Hediff_TerminalImplant : Hediff
    {
        public static bool HasTerminalImplant(Pawn pawn)
        {
            bool flag = pawn == null || pawn.health == null;
            return !flag && pawn.health.hediffSet.HasHediff(DefDatabase<HediffDef>.GetNamed("DigitalStorage_TerminalImplant", true), false);
        }

        public static void AddTerminalImplant(Pawn pawn)
        {
            bool flag = pawn == null || pawn.health == null;
            if (!flag)
            {
                bool flag2 = Hediff_TerminalImplant.HasTerminalImplant(pawn);
                if (!flag2)
                {
                    HediffDef hediffDef = DefDatabase<HediffDef>.GetNamed("DigitalStorage_TerminalImplant", true);
                    Hediff hediff = HediffMaker.MakeHediff(hediffDef, pawn, null);
                    pawn.health.AddHediff(hediff, null, null, null);
                }
            }
        }

        public static void RemoveTerminalImplant(Pawn pawn)
        {
            bool flag = pawn == null || pawn.health == null;
            if (!flag)
            {
                Hediff hediff = pawn.health.hediffSet.GetFirstHediffOfDef(DefDatabase<HediffDef>.GetNamed("DigitalStorage_TerminalImplant", true), false);
                bool flag2 = hediff != null;
                if (flag2)
                {
                    pawn.health.RemoveHediff(hediff);
                }
            }
        }

        public override void Notify_PawnKilled()
        {
            base.Notify_PawnKilled();
        }

        public override void ExposeData()
        {
            base.ExposeData();
        }
    }
}

