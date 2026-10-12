using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
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

            ApplySideEffects(psychicRitual, 1f - chance);
        }

        // A botched, low-quality ritual takes a hidden toll on those who performed it:
        // the invoker and every chanter each roll independently - 40% to silently catch
        // an oni infection, otherwise they receive a random creepjoiner downside.
        // Never reported in the ritual outcome.
        private void ApplySideEffects(PsychicRitual psychicRitual, float sideEffectChance)
        {
            try
            {
                var circle = psychicRitual.def as PsychicRitualDef_InvocationCircle;
                if (circle == null || sideEffectChance <= 0f || !Rand.Chance(sideEffectChance))
                    return;

                foreach (var role in new[] { circle.InvokerRole, circle.ChanterRole })
                {
                    if (role == null)
                        continue;
                    foreach (Pawn pawn in psychicRitual.assignments.AssignedPawns(role).ToList())
                    {
                        if (pawn == null || pawn.Dead || pawn.Destroyed)
                            continue;
                        if (Rand.Chance(0.40f) && !Oni.IsOni(pawn) && Oni.GetInfection(pawn) == null)
                        {
                            pawn.health.AddHediff(HediffMaker.MakeHediff(HediffDefOf_AoOni.AoOniInfection, pawn));
                            Log.Message("[AoOni] " + pawn.LabelShortCap + " silently caught an oni infection from the reconstitution ritual");
                        }
                        else
                        {
                            CreepJoinerDownsides.GrantRandomDownside(pawn);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] reconstitution side effect failed: " + e);
            }
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

                // Also purge a creepjoiner downside (its leftover traits and hediffs included)
                CreepJoinerDownsides.RemoveDownside(pawn);

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

    // Creepjoiner downside utilities: the reconstitution ritual purges the downside
    // from its target, and its hidden side effect can bestow a random downside on
    // the performers. Granting gives the pawn a real creepjoiner tracker (if they
    // never had one) so the downside behaves exactly like on a born creepjoiner:
    // its traits, hediffs and abilities apply immediately, its worker is created,
    // and the trigger schedule is set as on joining the colony.
    public static class CreepJoinerDownsides
    {
        // Excluded from the random pool: "no downside" at all, and the metalhorror
        // downside whose quest-only worker would never trigger outside its quest.
        private static readonly HashSet<string> PoolExclusions = new HashSet<string> { "Nothing", "Metalhorror" };

        public static void GrantRandomDownside(Pawn pawn)
        {
            try
            {
                if (pawn == null || pawn.Dead || pawn.Destroyed || !pawn.RaceProps.Humanlike || !ModsConfig.AnomalyActive)
                    return;

                var pool = DefDatabase<CreepJoinerDownsideDef>.AllDefsListForReading
                    .Where(d => !PoolExclusions.Contains(d.defName)).ToList();
                if (pool.Count == 0)
                    return;
                CreepJoinerDownsideDef downside = pool.RandomElementByWeight(d => d.Weight);

                Pawn_CreepJoinerTracker tracker = pawn.creepjoiner;
                if (tracker == null)
                {
                    tracker = new Pawn_CreepJoinerTracker(pawn);
                    pawn.creepjoiner = tracker;
                }
                // Fill the remaining slots so vanilla never dereferences a null def
                // (e.g. the tracker's killed/surgical-inspection hooks).
                if (tracker.form == null)
                    tracker.form = DefDatabase<CreepJoinerFormKindDef>.AllDefsListForReading.RandomElementByWeight(f => f.Weight);
                if (tracker.benefit == null)
                    tracker.benefit = DefDatabase<CreepJoinerBenefitDef>.AllDefsListForReading.RandomElementByWeight(b => b.Weight);
                if (tracker.aggressive == null)
                    tracker.aggressive = DefDatabase<CreepJoinerAggressiveDef>.AllDefsListForReading.RandomElementByWeight(a => a.Weight);
                if (tracker.rejection == null)
                    tracker.rejection = DefDatabase<CreepJoinerRejectionDef>.AllDefsListForReading.RandomElementByWeight(r => r.Weight);

                tracker.downside = downside;

                // Immediate effects, mirroring vanilla creepjoiner creation
                foreach (BackstoryTrait trait in downside.traits)
                {
                    if (pawn.story != null && !pawn.story.traits.HasTrait(trait.def))
                        pawn.story.traits.GainTrait(new Trait(trait.def, trait.degree, true));
                }
                foreach (HediffDef hediffDef in downside.hediffs)
                    pawn.health.AddHediff(hediffDef);
                if (pawn.abilities != null)
                {
                    foreach (AbilityDef ability in downside.abilities)
                        pawn.abilities.GainAbility(ability);
                }

                // Worker-applied effects (e.g. the Ao Oni downside marks the pawn as a
                // doppelganger). Vanilla only calls OnCreated from Notify_Created, so
                // this manual call is the only one.
                if (downside.workerType != null && typeof(BaseCreepJoinerWorker).IsAssignableFrom(downside.workerType))
                {
                    var worker = (BaseCreepJoinerWorker)Activator.CreateInstance(downside.workerType);
                    worker.Tracker = tracker;
                    worker.OnCreated();
                }

                // Schedule the downside exactly like Notify_ChangedFaction does for a
                // creepjoiner colonist (the timer fields are private).
                if (downside.triggersAfterDays != FloatRange.Zero)
                    SetTimerField(tracker, "downsideTriggersAt", GenTicks.TicksGame + (int)(downside.triggersAfterDays.RandomInRange * 60000f));
                if (downside.triggerMinDays != FloatRange.Zero)
                    SetTimerField(tracker, "canTriggerDownsideAfter", GenTicks.TicksGame + (int)(downside.triggerMinDays.RandomInRange * 60000f));

                Log.Message("[AoOni] " + pawn.LabelShortCap + " silently received the creepjoiner downside '" + downside.defName + "'");
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] granting a creepjoiner downside failed: " + e);
            }
        }

        private static void SetTimerField(Pawn_CreepJoinerTracker tracker, string field, int value)
        {
            AccessTools.Field(typeof(Pawn_CreepJoinerTracker), field)?.SetValue(tracker, value);
        }

        // Clear the tracked downside and remove what it directly applied. Anything the
        // downside triggered later (bad hediffs, negative traits) is handled by the
        // ritual's generic purge that runs just before this.
        public static void RemoveDownside(Pawn pawn)
        {
            try
            {
                var tracker = pawn?.creepjoiner;
                if (tracker == null)
                    return;
                CreepJoinerDownsideDef downside = tracker.downside;
                tracker.downside = null;
                if (downside == null)
                    return;

                if (pawn.story != null)
                {
                    foreach (BackstoryTrait trait in downside.traits)
                    {
                        Trait existing = pawn.story.traits.allTraits.FirstOrDefault(t => t.def == trait.def);
                        if (existing != null)
                            pawn.story.traits.RemoveTrait(existing);
                    }
                }
                foreach (HediffDef hediffDef in downside.hediffs)
                {
                    Hediff hediff = pawn.health.hediffSet.GetFirstHediffOfDef(hediffDef);
                    if (hediff != null)
                        pawn.health.RemoveHediff(hediff);
                }
                Log.Message("[AoOni] " + pawn.LabelShortCap + " was purged of the creepjoiner downside '" + downside.defName + "'");
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] removing a creepjoiner downside failed: " + e);
            }
        }
    }

    // While the reconstitution ritual is channeling, any oni infection carried by
    // the invoker, the target or a chanter is held in stasis - the infection makes
    // no progress for the duration of the rite. Applied every tick from the
    // InvokeHorax patch in Patches.cs.
    public static class RitualInfectionHold
    {
        private static readonly Dictionary<Pawn, int> frozenAges = new Dictionary<Pawn, int>();

        public static bool IsActive(PsychicRitual ritual)
        {
            return ritual?.def is PsychicRitualDef_AoOniReconstitution;
        }

        public static IEnumerable<Pawn> Participants(PsychicRitual ritual)
        {
            var circle = ritual.def as PsychicRitualDef_InvocationCircle;
            if (circle == null)
                yield break;
            foreach (PsychicRitualRoleDef role in new[] { circle.InvokerRole, circle.TargetRole, circle.ChanterRole })
            {
                if (role == null)
                    continue;
                foreach (Pawn pawn in ritual.assignments.AssignedPawns(role))
                    yield return pawn;
            }
        }

        public static void Tick(PsychicRitual ritual)
        {
            try
            {
                if (!IsActive(ritual))
                    return;
                foreach (Pawn pawn in Participants(ritual))
                {
                    List<Hediff_AoOniInfection> infections = pawn?.health?.hediffSet?.hediffs
                        ?.OfType<Hediff_AoOniInfection>().ToList();
                    if (infections == null || infections.Count == 0)
                    {
                        frozenAges.Remove(pawn);
                        continue;
                    }
                    int frozen;
                    if (!frozenAges.TryGetValue(pawn, out frozen))
                    {
                        frozenAges[pawn] = infections.Max(h => h.ageTicks);
                        continue;
                    }
                    foreach (Hediff_AoOniInfection infection in infections)
                    {
                        if (infection.ageTicks > frozen)
                            infection.ageTicks = frozen;
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] infection hold failed: " + e);
            }
        }

        public static void End(PsychicRitual ritual)
        {
            try
            {
                if (!IsActive(ritual))
                    return;
                foreach (Pawn pawn in Participants(ritual).ToList())
                    frozenAges.Remove(pawn);
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] infection hold cleanup failed: " + e);
            }
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
