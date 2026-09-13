# -*- coding: utf-8 -*-
import os
os.chdir(r"D:\SteamLibrary\steamapps\common\RimWorld\Mods\Aooni")

# ============ Ritual def ============
open("Defs/PsychicRitualDefs/Ritual_AoOni.xml", "w", encoding="utf-8").write('''<?xml version="1.0" encoding="utf-8"?>
<Defs>

  <!-- Human reconstitution: burn a shard to rebuild a colonist.
       Removes all bad traits, wounds and diseases (incl. oni infection).
       Failing on a normal colonist destroys them; failing on a doppelganger
       triggers the transformation immediately. -->
  <PsychicRitualDef Class="AoOni.PsychicRitualDef_AoOniReconstitution">
    <defName>AoOniReconstitution</defName>
    <label>human reconstitution</label>
    <description>Burn a shard and psychically reconstitute a colonist from the void's blueprints. Every scar, sickness and dark trait is rewritten away - even an oni infection.\n\nBut the void does not tolerate forgery: should the ritual fail on a doppelganger, its true form will surface immediately.</description>
    <researchPrerequisite>AoOniInfectionCure</researchPrerequisite>
    <outcomeDescription>With the current quality, there's a {0} chance the reconstitution will succeed. A failure disintegrates a normal colonist - and a doppelganger transforms at once.</outcomeDescription>
    <iconPath>UI/Rituals/Reconstitution</iconPath>
    <hoursUntilOutcome>6</hoursUntilOutcome>
    <targetRole>DeathRefusalTarget</targetRole>
    <requiredOffering>
      <filter>
        <thingDefs>
          <li>Shard</li>
        </thingDefs>
      </filter>
      <count>1</count>
    </requiredOffering>
    <cooldownHours>360</cooldownHours>
    <chanterRole>ChanterAdvanced</chanterRole>
    <successChanceFromQualityCurve>
      <points>
        <li>(0, 0.35)</li>
        <li>(0.5, 0.70)</li>
        <li>(0.9, 0.95)</li>
        <li>(1, 1.0)</li>
      </points>
    </successChanceFromQualityCurve>
  </PsychicRitualDef>

</Defs>
''')
print("ritual xml ok")

# ============ C# additions ============
p = "Source/AoOni/AoOniEntities.cs"
s = open(p, encoding="utf-8").read()

old = '''    // Creepjoiner downside: the joiner is secretly a doppelganger.'''
new = '''    // ============ Human reconstitution ritual ============

    public class PsychicRitualDef_AoOniReconstitution : PsychicRitualDef_InvocationCircle
    {
        public SimpleCurve successChanceFromQualityCurve;

        public override List<PsychicRitualToil> CreateToils(PsychicRitual psychicRitual, PsychicRitualGraph graph)
        {
            List<PsychicRitualToil> list = base.CreateToils(psychicRitual, graph);
            list.Add(new PsychicRitualToil_AoOniReconstitution(TargetRole));
            return list;
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string item in base.ConfigErrors())
                yield return item;
            if (successChanceFromQualityCurve == null)
                yield return "successChanceFromQualityCurve does not exist";
        }
    }

    public class PsychicRitualToil_AoOniReconstitution : PsychicRitualToil
    {
        private PsychicRitualRoleDef targetRole;

        public PsychicRitualToil_AoOniReconstitution()
        {
        }

        public PsychicRitualToil_AoOniReconstitution(PsychicRitualRoleDef targetRole)
        {
            this.targetRole = targetRole;
        }

        public override void Start(PsychicRitual psychicRitual, PsychicRitualGraph graph)
        {
            var def = (PsychicRitualDef_AoOniReconstitution)psychicRitual.def;
            float chance = def.successChanceFromQualityCurve.Evaluate(psychicRitual.PowerPercent);

            foreach (Pawn pawn in psychicRitual.assignments.AssignedPawns(targetRole))
                ApplyOutcome(pawn, Rand.Chance(chance));
        }

        private void ApplyOutcome(Pawn pawn, bool success)
        {
            bool doppel = Oni.GetDoppelganger(pawn) != null;

            if (success)
            {
                // Remove every bad hediff: wounds, scars, diseases, infection, doppelganger mark
                List<Hediff> toRemove = new List<Hediff>();
                foreach (Hediff hediff in pawn.health.hediffSet.hediffs)
                {
                    if (hediff.def.isBad || hediff is Hediff_AoOniInfection || hediff is Hediff_Doppelganger)
                        toRemove.Add(hediff);
                }
                foreach (Hediff hediff in toRemove)
                    pawn.health.RemoveHediff(hediff);

                // Remove negative traits
                List<Trait> traits = new List<Trait>(pawn.story.traits.allTraits);
                foreach (Trait trait in traits)
                {
                    if (pawn.story.traits.DegreeOfTrait(trait) < 0)
                        pawn.story.traits.RemoveTrait(trait);
                }

                if (PawnUtility.ShouldSendNotificationAbout(pawn))
                    Find.LetterStack.ReceiveLetter(
                        "AoOni_ReconstitutionSuccessLabel".Translate(),
                        "AoOni_ReconstitutionSuccess".Translate(pawn.Named("PAWN")),
                        LetterDefOf.PositiveEvent, pawn);
                Log.Message("[AoOni] " + pawn.LabelShortCap + " was reconstituted" + (doppel ? " (doppelganger reverted)" : ""));
            }
            else if (doppel)
            {
                // The forgery is torn apart - transform immediately
                Find.LetterStack.ReceiveLetter(
                    "AoOni_ReconstitutionFailLabel".Translate(),
                    "AoOni_ReconstitutionDoppelgangerFail".Translate(pawn.Named("PAWN")),
                    LetterDefOf.ThreatBig, pawn);
                Transformer.TransformPawn(pawn);
            }
            else
            {
                // Normal colonist disintegrates
                Find.LetterStack.ReceiveLetter(
                    "AoOni_ReconstitutionFailLabel".Translate(),
                    "AoOni_ReconstitutionFail".Translate(pawn.Named("PAWN")),
                    LetterDefOf.ThreatBig, pawn);
                pawn.Destroy(DestroyMode.Vanish);
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref targetRole, "targetRole");
        }
    }

    // Creepjoiner downside: the joiner is secretly a doppelganger.'''
assert old in s, "ritual cs anchor"
s = s.replace(old, new, 1)
open(p, "w", encoding="utf-8").write(s)
print("ritual cs ok")
