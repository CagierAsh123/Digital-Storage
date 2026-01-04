using System;
using System.Collections.Generic;
using DigitalStorage.Components;
using Verse;

namespace DigitalStorage.Services
{
    public static class PawnStorageAccess
    {
        public static bool HasTerminalImplant(Pawn pawn)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
            {
                return false;
            }

            // 1. 检查 Pawn 自己是否有终端芯片
            if (pawn.health.hediffSet.HasHediff(DefDatabase<HediffDef>.GetNamed("DigitalStorage_TerminalImplant", false), false))
            {
                return true;
            }

            // 2. 如果是机械体，检查其监管者（机械师）是否有终端芯片
            if (pawn.IsColonyMech)
            {
                Pawn overseer = pawn.GetOverseer();
                if (overseer != null && overseer.health != null && overseer.health.hediffSet != null)
                {
                    if (overseer.health.hediffSet.HasHediff(DefDatabase<HediffDef>.GetNamed("DigitalStorage_TerminalImplant", false), false))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}

