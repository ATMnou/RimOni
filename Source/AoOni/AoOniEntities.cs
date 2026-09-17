using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace AoOni
{
    // ============ Vanilla surgical inspection: detects oni infection & doppelganger ============

    public class HediffCompProperties_SurgeryInspectable_AoOni : HediffCompProperties_SurgeryInspectable
    {
        public HediffCompProperties_SurgeryInspectable_AoOni()
        {
            compClass = typeof(HediffComp_SurgeryInspectable_AoOni);
        }
    }

    public class HediffComp_SurgeryInspectable_AoOni : HediffComp_SurgeryInspectable
    {
        private static bool ResearchDone =>
            DefDatabase<ResearchProjectDef>.GetNamed("AoOniInfectionTest", false)?.IsFinished ?? false;

        public override SurgicalInspectionOutcome DoSurgicalInspection(Pawn surgeon)
        {
            // Requires the "Ao Oni infection test" research
            if (!ResearchDone)
                return SurgicalInspectionOutcome.Nothing;

            Pawn patient = parent.pawn;
            if (patient == null)
                return SurgicalInspectionOutcome.Nothing;

            bool doppel = parent.def.defName == "AoOniDoppelganger";

            if (parent is Hediff_AoOniInfection infection)
                infection.Diagnosed = true;

            if (doppel)
            {
                if (patient.health.hediffSet.GetFirstHediffOfDef(HediffDefOf_AoOni.AoOniDoppelganger) is Hediff_Doppelganger dg)
                    dg.Diagnosed = true;

                // A diagnosed doppelganger develops its own oni infection (1.5 days) - not hidden
                var zombieDef = DefDatabase<HediffDef>.GetNamed("AoOniInfectionZombie", false);
                if (zombieDef != null && Oni.GetInfection(patient) == null)
                {
                    var inf = (Hediff_AoOniInfection)HediffMaker.MakeHediff(zombieDef, patient);
                    inf.Diagnosed = true;
                    patient.health.AddHediff(inf);
                    Log.Message("[AoOni] diagnosed doppelganger " + patient.LabelShortCap + " has developed an oni infection");
                }
            }

            Messages.Message("AoOni_TestPositive".Translate(patient.LabelShortCap),
                patient, MessageTypeDefOf.ThreatBig, true);
            Log.Message("[AoOni] surgical inspection detected " + parent.def.defName + " on " + patient.LabelShortCap);

            // We sent our own letter; suppress the vanilla one. The outer
            // Pawn.DoSurgicalInspection still calls SetVisible for this outcome.
            return SurgicalInspectionOutcome.DetectedNoLetter;
        }

        public override void DoSurgicalInspectionVisible(Pawn surgeon)
        {
            // Already discovered: no repeat report
        }
    }

    // ============ Human reconstitution ritual ============

    public class PsychicRitualDef_AoOniReconstitution : PsychicRitualDef_InvocationCircle
    {
        public SimpleCurve successChanceFromQualityCurve;

        public override List<PsychicRitualToil> CreateToils(PsychicRitual psychicRitual, PsychicRitualGraph graph)
        {
            List<PsychicRitualToil> list = base.CreateToils(psychicRitual, graph);
            list.Add(new PsychicRitualToil_AoOniReconstitution(TargetRole));
            return list;
        }

        public override TaggedString OutcomeDescription(FloatRange qualityRange, string qualityNumber, PsychicRitualRoleAssignments assignments)
        {
            return outcomeDescription.Formatted(successChanceFromQualityCurve.Evaluate(qualityRange.min).ToStringPercent());
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
                    if (trait.Suppressed)
                        continue;
                    bool negative = trait.Degree < 0;
                    if (!negative)
                    {
                        TraitDegreeData data = trait.def.DataAtDegree(trait.Degree);
                        if (data != null && data.statOffsets != null)
                        {
                            foreach (StatModifier mod in data.statOffsets)
                            {
                                if (mod.stat != null && mod.stat.defName == "Mood" && mod.value < 0)
                                    negative = true;
                            }
                        }
                    }
                    if (negative)
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

    // Creepjoiner downside: the joiner is secretly a doppelganger.
    public class CreepJoinerWorker_AoOniDownside : BaseCreepJoinerWorker
    {
        public override void OnCreated()
        {
            if (base.Pawn == null || base.Pawn.health == null)
                return;
            base.Pawn.health.AddHediff(HediffMaker.MakeHediff(HediffDefOf_AoOni.AoOniDoppelganger, base.Pawn));
            Log.Message("[AoOni] a creepjoiner arrived as a doppelganger");
        }

        public override void DoResponse(List<TargetInfo> looktargets, List<NamedArgument> namedArgs)
        {
        }
    }

    // Oni nucleus: behaves like a fleshmass nucleus, but activates into a monstrous oni.
    public class CompProperties_AoOniNucleus : CompProperties_FleshmassNucleus
    {
        public CompProperties_AoOniNucleus()
        {
            compClass = typeof(CompAoOniNucleus);
        }
    }

    public class CompAoOniNucleus : CompFleshmassNucleus, RimWorld.IActivity
    {
        public new void OnActivityActivated()
        {
            try
            {
                Pawn nucleus = Pawn;
                Map map = nucleus.MapHeld;
                IntVec3 cell = nucleus.PositionHeld;
                if (map != null)
                {
                    Transformer.SpawnOni(map, cell, "AoOni_MonstrousOni", 1);
                    nucleus.Destroy(DestroyMode.Vanish);
                }
            }
            catch (System.Exception e)
            {
                Log.Error("[AoOni] nucleus activation failed: " + e);
            }
        }
    }

    // Surgical cure: removes an Oni infection from a living patient.
    public class RecipeWorker_AoOniCure : Recipe_Surgery
    {
        public override void ApplyOnPawn(Pawn pawn, BodyPartRecord part, Pawn billDoer, List<Thing> ingredients, Bill bill)
        {
            if (pawn == null || pawn.Dead)
                return;

            // A doppelganger cannot be cured - attempting it reveals its true form at once
            if (Oni.GetDoppelganger(pawn) != null)
            {
                Find.LetterStack.ReceiveLetter(
                    "AoOni_ReconstitutionFailLabel".Translate(),
                    "AoOni_CureDoppelganger".Translate(pawn.Named("PAWN")),
                    LetterDefOf.ThreatBig, pawn);
                Transformer.TransformPawn(pawn);
                return;
            }

            var infection = Oni.GetInfection(pawn);
            if (infection == null)
            {
                Messages.Message("AoOni_TestNegative".Translate(pawn.LabelShortCap),
                    pawn, MessageTypeDefOf.PositiveEvent, true);
                return;
            }

            pawn.health.RemoveHediff(infection);
            var changing = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf_AoOni.AoOniChanging);
            if (changing != null)
                pawn.health.RemoveHediff(changing);

            Messages.Message("AoOni_Cured".Translate(pawn.LabelShortCap),
                pawn, MessageTypeDefOf.PositiveEvent, true);
        }
    }

    // Infection hediff: the victim turns into an Oni after 3 days (1.5 days for the zombie variant).
    public class Hediff_AoOniInfection : HediffWithComps
    {
        private bool diagnosed;

        public bool Diagnosed
        {
            get => diagnosed;
            set => diagnosed = true;
        }

        public int TransformTicks
        {
            get
            {
                if (def.defName == "AoOniInfectionZombie")
                    return 90000;   // 1.5 days
                if (def.defName == "AoOniInfectionFast")
                    return 10000;   // 4 hours
                return 180000;      // 3 days
            }
        }

        // Hidden until diagnosed by the surgical test
        public override bool Visible => diagnosed;

        public override string LabelBase => diagnosed ? base.LabelBase : "??";

        public override void Tick()
        {
            base.Tick();
            // Severity tracks elapsed time so progress is visible once diagnosed
            float target = Mathf.Min(1f, ageTicks / (float)TransformTicks);
            if (Severity < target)
                Severity = target;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref diagnosed, "diagnosed", false);
        }
    }

    public class Hediff_Doppelganger : HediffWithComps
    {
        private bool diagnosed;

        public bool Diagnosed
        {
            get => diagnosed;
            set => diagnosed = true;
        }

        public override bool Visible => diagnosed;

        public override string LabelBase => diagnosed ? base.LabelBase : "??";

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref diagnosed, "diagnosed", false);
        }
    }

    // Renders the oni texture as authored; victims carrying the "changing" marker turn purple.
    public class PawnRenderNode_AoOni : PawnRenderNode
    {
        public PawnRenderNode_AoOni(Pawn pawn, PawnRenderNodeProperties props, PawnRenderTree tree)
            : base(pawn, props, tree)
        {
        }

        public override Color ColorFor(Pawn pawn)
        {
            if (pawn != null && pawn.health?.hediffSet?.HasHediff(HediffDefOf_AoOni.AoOniChanging) == true)
                return new Color(0.55f, 0.2f, 0.85f);
            return Color.white;
        }
    }

    // Hostile oni raid
    public class IncidentWorker_AoOniRaid : IncidentWorker
    {
        protected override bool CanFireNowSub(IncidentParms parms)
        {
            return Current.Game.Maps.Any(m => m.IsPlayerHome);
        }

        protected override bool TryExecuteWorker(IncidentParms parms)
        {
            var map = parms.target as Map;
            if (map == null)
                return false;

            float points = parms.points;
            if (points <= 0f)
                points = StorytellerUtility.DefaultThreatPointsNow(map);
            points = Mathf.Clamp(points, 150f, 2500f);

            if (Faction.OfEntities == null)
                return false;

            var pool = DefDatabase<PawnKindDef>.AllDefs
                .Where(k => k.defName.StartsWith("AoOni_") && k.defName != "AoOni_PuppetOni" && k.defName != "AoOni_ThousandsHandOni" && k.defName != "AoOni_Nucleus")
                .ToList();

            float baseW = AoOniMod.settings?.baseOniChance ?? 0.30f;
            bool cold = map.mapTemperature.OutdoorTemp < 0f;

            var spawned = new List<Pawn>();
            var edge = CellFinder.RandomEdgeCell(map);

            int guard = 0;
            while (points >= 90f && guard++ < 20)
            {
                string choice;
                if (Rand.Chance(baseW))
                {
                    choice = "AoOni_AoOni";
                }
                else
                {
                    var others = pool.Where(k => k.defName != "AoOni_AoOni").ToList();
                    if (cold && Rand.Chance(0.4f))
                        choice = Rand.Chance(0.5f) ? "AoOni_IceOni" : "AoOni_ZombieOni";
                    else
                        choice = others.RandomElement().defName;
                }

                var kind = DefDatabase<PawnKindDef>.GetNamed(choice, true);
                var request = new PawnGenerationRequest(kind, Faction.OfEntities, PawnGenerationContext.NonPlayer, map.Tile);
                var pawn = PawnGenerator.GeneratePawn(request);
                var cell = CellFinder.RandomClosewalkCellNear(edge, map, 3);
                GenSpawn.Spawn(pawn, cell.IsValid ? cell : edge, map, Rot4.Random, WipeMode.Vanish, false);
                spawned.Add(pawn);

                points -= kind.combatPower;
            }

            if (spawned.Count == 0)
                return false;

            SendStandardLetter(parms, new LookTargets(spawned));
            return true;
        }
    }
}
