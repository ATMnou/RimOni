using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AoOni
{
    // Infection on being damaged by an Oni (only if the armor did not fully block it),
    // and puppet oni double damage against mechanoids.
    [HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
    public static class Pawn_TakeDamage_AoOniPatch
    {
        public static void Prefix(Thing __instance, ref DamageInfo dinfo, out float __state)
        {
            __state = -1f;

            var pawn = __instance as Pawn;
            if (pawn == null)
                return;

            // Puppet oni: double damage against mechanoids
            var instigator = dinfo.Instigator as Pawn;
            if (instigator != null && instigator.def.defName == "AoOni_PuppetOni" && pawn.RaceProps.IsMechanoid)
            {
                dinfo.SetAmount(dinfo.Amount * 2f);
            }

            // Squatto oni: quadruple damage against buildings and walls
            if (instigator != null && instigator.def.defName == "AoOni_SquattoOni" && __instance is Building)
            {
                dinfo.SetAmount(dinfo.Amount * 4f);
            }

            __state = pawn.health.summaryHealth.SummaryHealthPercent;
        }

        public static void Postfix(Thing __instance, DamageInfo dinfo, float __state)
        {
            try
            {
                var pawn = __instance as Pawn;
                if (pawn == null || __state < 0f || pawn.Dead || pawn.Destroyed)
                    return;
                if (__state - pawn.health.summaryHealth.SummaryHealthPercent <= 0.0001f)
                    return; // fully blocked by armor

                var attacker = dinfo.Instigator as Pawn;
                if (attacker == null || !Oni.IsOni(attacker))
                    return;
                Oni.ApplyInfection(pawn, attacker);
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] TakeDamage patch failed: " + e);
            }
        }
    }

    // Doppelganger: colonists returning from a caravan alone or as an "unarmed" group
    // have a chance of being replaced. Only caravans arriving on foot count - drop pods
    // and shuttles are exempt.
    // "Unarmed" means: no colonist carrying a weapon, no armed escorts. Biotech non-combat
    // mechs count as unarmed; any other mech counts as armed. Animals are armed if they
    // have attack (release) or obedience training.
    [HarmonyPatch]
    public static class Caravan_AoOniDoppelPatch
    {
        public static bool prepared;

        // "Slept during the journey" is evaluated once per return event
        private static bool appliedSleepBonus;

        public static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("RimWorld.Planet.CaravanEnterMapUtility");
            if (type == null)
                return null;
            return AccessTools.Method(type, "Enter", new[]
            {
                typeof(RimWorld.Planet.Caravan), typeof(Map), typeof(RimWorld.Planet.CaravanEnterMode),
                typeof(RimWorld.Planet.CaravanDropInventoryMode), typeof(bool), typeof(System.Predicate<Verse.IntVec3>)
            });
        }

        public static void Postfix(Caravan caravan)
        {
            try
            {
                if (prepared || AoOniMod.settings == null || !AoOniMod.settings.doppelgangerJoin)
                    return;
                if (caravan == null || caravan.Faction != Faction.OfPlayer)
                    return;

                var all = caravan.PawnsListForReading.Where(p => !p.Destroyed).ToList();
                TryDoppel(all, all.Count);
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] doppelganger patch failed: " + e);
            }
        }

        public static void TryDoppel(IEnumerable<Pawn> pawns, int totalEntries)
        {
            appliedSleepBonus = false;

            // Requires the void monolith at level 1+
            if (ModsConfig.AnomalyActive && Find.Anomaly.HighestLevelReached < 1)
                return;
            var members = pawns.Where(p => p.IsColonist && p.RaceProps.Humanlike && !p.Destroyed).ToList();
            if (members.Count == 0 || members.Count > 2)
                return;
            if (totalEntries > 2)
                return;
            // Anyone "armed" cancels the strike
            foreach (var m in pawns)
            {
                if (m.RaceProps.Humanlike && m.equipment?.Primary != null)
                    return;
                if (m.RaceProps.IsMechanoid && m.RaceProps.FleshType != FleshTypeDefOf.Mechanoid)
                    return;
                if (IsArmedMech(m) || IsArmedAnimal(m))
                    return;
            }

            float chance = 0.20f;
            // If anyone actually slept at some point during the journey: +10%.
            // Awake() is false for sleeping pawns (drowsy-but-awake pawns don't count).
            // The bonus only applies once per return event.
            if (!appliedSleepBonus && pawns.Any(p => !p.Awake()))
            {
                appliedSleepBonus = true;
                chance += 0.10f;
            }

            if (!Rand.Chance(chance))
                return;

            var victim = members.RandomElement();
            victim.health.AddHediff(HediffMaker.MakeHediff(HediffDefOf_AoOni.AoOniDoppelganger, victim));
            Log.Message("[AoOni] a doppelganger may have infiltrated with the returning group");
        }

        // Biotech colony mechs: non-fighter kinds (lifter, constructoid, fabricor, agrihand...)
        // count as unarmed; everything else counts as armed.
        private static bool IsArmedMech(Pawn p)
        {
            if (!p.RaceProps.IsMechanoid)
                return false;
            var kind = p.kindDef;
            if (kind != null && !kind.isFighter)
                return false;
            return true;
        }

        private static bool IsArmedAnimal(Pawn p)
        {
            if (!p.RaceProps.Animal || p.training == null)
                return false;
            if (p.training.HasLearned(TrainableDefOf.Release))
                return true;
            if (p.training.HasLearned(TrainableDefOf.Obedience))
                return true;
            return false;
        }
    }

    // Death hooks: eggplant oni drops food, ice oni revives as hunchback oni
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class Pawn_Kill_AoOniPatch
    {
        // The pawn despawns during Kill, so capture map/position beforehand.
        // (Harmony allows only a single __state, so use a static map instead.)
        private static readonly Dictionary<Pawn, (Map map, IntVec3 cell)> killCtx
            = new Dictionary<Pawn, (Map, IntVec3)>();

        public static void Prefix(Pawn __instance)
        {
            killCtx[__instance] = (__instance.Map, __instance.Position);
        }

        public static void Postfix(Pawn __instance)
        {
            try
            {
                if (!killCtx.TryGetValue(__instance, out var ctx))
                    return;
                killCtx.Remove(__instance);

                // A doppelganger killed outright (without transforming) bursts into an oni
                if (__instance.health?.hediffSet?.GetFirstHediffOfDef(HediffDefOf_AoOni.AoOniDoppelganger) != null)
                {
                    Map dmap = ctx.map;
                    if (dmap != null && !dmap.Disposed)
                    {
                        foreach (Thing t in dmap.thingGrid.ThingsAt(ctx.cell))
                        {
                            if (t is Corpse corpse && corpse.InnerPawn == __instance)
                            {
                                corpse.Destroy(DestroyMode.Vanish);
                                break;
                            }
                        }
                        Transformer.SpawnOni(dmap, ctx.cell, "AoOni_AoOni", 1);
                        Find.LetterStack.ReceiveLetter(
                            "AoOni_ReconstitutionFailLabel".Translate(),
                            "AoOni_DoppelDeath".Translate(__instance.Named("PAWN")),
                            LetterDefOf.ThreatBig, new LookTargets(ctx.cell, dmap));
                        Log.Message("[AoOni] a slain doppelganger revealed its true form");
                    }
                    return;
                }

                if (!Oni.IsOni(__instance))
                    return;
                Map map = ctx.map;
                if (map == null || map.Disposed)
                    return;
                IntVec3 cell = ctx.cell;

                if (__instance.def.defName == "AoOni_EggPlantOni")
                {
                    var foods = DefDatabase<ThingDef>.AllDefs.Where(d =>
                        d.thingCategories != null &&
                        d.thingCategories.Any(c => c.defName == "FoodRaw" || c.defName == "MeatRaw" ||
                                                   c.defName == "PlantFoodRaw" || c.defName == "AnimalProductRaw" ||
                                                   c.defName == "EggsUnfertilized" || c.defName == "EggsFertilized") &&
                        d.ingestible != null && !d.IsCorpse).ToList();
                    if (foods.Count > 0)
                    {
                        var food = foods.RandomElement();
                        var thing = ThingMaker.MakeThing(food);
                        thing.stackCount = Mathf.Min(food.stackLimit, Rand.RangeInclusive(20, 40));
                        GenPlace.TryPlaceThing(thing, cell, map, ThingPlaceMode.Near);
                    }
                }

                if (__instance.def.defName == "AoOni_IceOni")
                {
                    Transformer.SpawnOni(map, cell, "AoOni_HunchbackOni", 1);
                }

                if (__instance.def.defName == "AoOni_PlumpyOni")
                {
                    AoOniGameComp.SpawnBerryGas(map, cell);
                }

                if (__instance.def.defName == "AoOni_MonstrousOni")
                {
                    // Destroyed monstrous oni leaves its nucleus behind
                    var request = new PawnGenerationRequest(
                        DefDatabase<PawnKindDef>.GetNamed("AoOni_Nucleus", true),
                        Faction.OfEntities, PawnGenerationContext.NonPlayer, map.Tile);
                    var nucleus = PawnGenerator.GeneratePawn(request);
                    GenSpawn.Spawn(nucleus, cell, map, Rot4.Random, WipeMode.Vanish, false);
                    Log.Message("[AoOni] a destroyed monstrous oni left its nucleus");
                }
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] Kill patch failed: " + e);
            }
        }
    }

    // Mechanoid repair purges the Oni infection
    [HarmonyPatch(typeof(RimWorld.MechRepairUtility), nameof(RimWorld.MechRepairUtility.RepairTick), new[] { typeof(Pawn) })]
    public static class MechRepairUtility_AoOniPatch
    {
        public static void Postfix(Pawn mech)
        {
            try
            {
                if (mech == null || mech.Dead || mech.Destroyed)
                    return;
                var infection = Oni.GetInfection(mech);
                if (infection == null)
                    return;

                mech.health.RemoveHediff(infection);
                var changing = mech.health.hediffSet.GetFirstHediffOfDef(HediffDefOf_AoOni.AoOniChanging);
                if (changing != null)
                    mech.health.RemoveHediff(changing);
                Log.Message("[AoOni] " + mech.LabelShortCap + " purged its Oni infection during repair.");
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] repair patch failed: " + e);
            }
        }
    }

    // Register the infection test / cure surgeries on every pawn race
    [HarmonyPatch(typeof(PlayDataLoader), nameof(PlayDataLoader.LoadAllPlayData))]
    public static class PlayDataLoader_AoOniPatch
    {
        public static void Postfix()
        {
            try
            {
                var cure = DefDatabase<RecipeDef>.GetNamed("AoOniCure", false);
                if (cure == null)
                    return;

                int n = 0;
                foreach (var def in DefDatabase<ThingDef>.AllDefs)
                {
                    if (def.race == null)
                        continue;
                    n++;
                    if (def.recipes == null)
                        def.recipes = new List<RecipeDef>();
                    if (!def.recipes.Contains(cure))
                        def.recipes.Add(cure);
                    AccessTools.Field(typeof(ThingDef), "allRecipesCached")?.SetValue(def, null);
                }
                Log.Message($"[AoOni] registered the cure surgery on {n} pawn races");
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] surgery registration failed: " + e);
            }
        }
    }


    // Fishing: jellyfish / blue shrimp oni lurk beneath the surface.
    // Base 5% chance, reduced to 1% by high artistic skill, removed by the
    // "careful fishing" research.
    [HarmonyPatch(typeof(RimWorld.FishingUtility), nameof(RimWorld.FishingUtility.GetNegativeFishingOutcomes))]
    public static class FishingUtility_AoOniPatch
    {
        public static void Postfix(Pawn pawn, List<RimWorld.NegativeFishingOutcomeDef> __result)
        {
            try
            {
                if (__result == null || pawn == null || pawn.Dead || pawn.Map == null)
                    return;

                // Requires the void monolith at level 1+
                if (ModsConfig.AnomalyActive && Find.Anomaly.HighestLevelReached < 1)
                    return;

                var careful = DefDatabase<ResearchProjectDef>.GetNamed("AoOniCarefulFishing", false);
                if (careful != null && careful.IsFinished)
                    return;

                float skill = pawn.skills?.GetSkill(SkillDefOf.Artistic)?.Level ?? 0;
                float chance = 0.05f - 0.04f * Mathf.Clamp01(skill / 20f);
                if (!Rand.Chance(chance))
                    return;

                Map map = pawn.Map;
                string variant = Rand.Chance(0.5f) ? "AoOni_JellyfishOni" : "AoOni_BlueShrimp";
                Transformer.SpawnOni(map, pawn.Position, variant, 1);

                foreach (var p in map.mapPawns.AllPawnsSpawned)
                {
                    if (Oni.IsOni(p) && p.Position.InHorDistOf(pawn.Position, 8f))
                        p.stances.stunner.StunFor(300, null);
                }

                Find.LetterStack.ReceiveLetter(
                    "AoOni_FishingLetterLabel".Translate(),
                    "AoOni_FishingLetter".Translate(pawn.Named("PAWN")),
                    LetterDefOf.ThreatBig, new LookTargets(pawn));
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] fishing patch failed: " + e);
            }
        }
    }


    // ============ Doppelganger contamination ============

    // Meals cooked by a doppelganger infect whoever eats them.
    [HarmonyPatch(typeof(GenRecipe), "PostProcessProduct")]
    public static class GenRecipe_AoOniTaintPatch
    {
        public static void Postfix(Thing __result, Pawn worker)
        {
            try
            {
                if (__result == null || worker == null || AoOniGameComp.taintedThings == null)
                    return;
                if (__result.def.ingestible == null || __result.def.category != ThingCategory.Item)
                    return;
                if (Oni.GetDoppelganger(worker) == null)
                    return;

                AoOniGameComp.taintedThings.Add(__result.thingIDNumber);
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] taint patch failed: " + e);
            }
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.Ingested))]
    public static class Thing_Ingested_AoOniPatch
    {
        public static void Postfix(Thing __instance, Pawn ingester)
        {
            try
            {
                if (AoOniGameComp.taintedThings == null || ingester == null || ingester.Dead || ingester.Destroyed)
                    return;
                if (!AoOniGameComp.taintedThings.Remove(__instance.thingIDNumber))
                    return;
                if (Oni.IsOni(ingester) || Oni.GetInfection(ingester) != null)
                    return;

                var infectionDef = DefDatabase<HediffDef>.GetNamed("AoOniInfection", false);
                if (infectionDef != null)
                {
                    ingester.health.AddHediff(HediffMaker.MakeHediff(infectionDef, ingester));
                    Log.Message("[AoOni] " + ingester.LabelShortCap + " infected by food cooked by a doppelganger");
                }
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] ingestion patch failed: " + e);
            }
        }
    }

    // Surgeries performed by a doppelganger always infect the patient.
    [HarmonyPatch(typeof(RecipeWorker), nameof(RecipeWorker.ApplyOnPawn))]
    public static class RecipeWorker_AoOniPatch
    {
        public static void Postfix(Pawn pawn, Pawn billDoer)
        {
            try
            {
                if (pawn == null || pawn.Dead || pawn.Destroyed || billDoer == null)
                    return;
                if (Oni.GetDoppelganger(billDoer) == null)
                    return;
                if (Oni.IsOni(pawn) || Oni.GetInfection(pawn) != null)
                    return;

                var infectionDef = DefDatabase<HediffDef>.GetNamed("AoOniInfection", false);
                if (infectionDef != null)
                {
                    pawn.health.AddHediff(HediffMaker.MakeHediff(infectionDef, pawn));
                    Log.Message("[AoOni] " + pawn.LabelShortCap + " infected by a surgery performed by a doppelganger");
                }
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] surgery taint patch failed: " + e);
            }
        }
    }

    // Reveal the oni codex page once the "onionomy" research completes
    [HarmonyPatch(typeof(RimWorld.ResearchManager), nameof(RimWorld.ResearchManager.FinishProject))]
    public static class ResearchManager_AoOniPatch
    {
        public static void Postfix(ResearchProjectDef proj)
        {
            try
            {
                if (proj != null && proj.defName == "AoOniBasics")
                    Oni.OnBasicsResearched();
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] research finish patch failed: " + e);
            }
        }
    }


    // ============ Vanilla/DLC items vs oni infection ============

    public static class AoOniCures
    {
        // Remove an oni infection (and its "changing" marker) from a pawn
        public static void CureInfection(Pawn pawn, string cause)
        {
            var infection = Oni.GetInfection(pawn);
            if (infection == null)
                return;
            pawn.health.RemoveHediff(infection);
            var changing = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf_AoOni.AoOniChanging);
            if (changing != null)
                pawn.health.RemoveHediff(changing);
            Log.Message("[AoOni] " + pawn.LabelShortCap + " cured of oni infection by " + cause);
        }

        public static bool HasInfection(Pawn pawn)
        {
            return Oni.GetInfection(pawn) != null;
        }
    }

    // Healer mech serum: heals the worst health condition - the oni infection counts.
    [HarmonyPatch(typeof(RimWorld.CompUseEffect_FixWorstHealthCondition), "DoEffect")]
    public static class HealerSerum_AoOniPatch
    {
        public static void Postfix(Pawn usedBy)
        {
            try
            {
                if (usedBy != null && !usedBy.Dead && !usedBy.Destroyed)
                    AoOniCures.CureInfection(usedBy, "healer mech serum");
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] healer serum patch failed: " + e);
            }
        }
    }

    // Unnatural healing (Anomaly psycast): heals the worst health condition - the
    // oni infection counts. The ability resolves through HealthUtility.FixWorstHealthCondition.
    [HarmonyPatch(typeof(Verse.HealthUtility), nameof(Verse.HealthUtility.FixWorstHealthCondition))]
    public static class UnnaturalHealing_AoOniPatch
    {
        public static void Postfix(Pawn pawn)
        {
            try
            {
                if (pawn != null && !pawn.Dead && !pawn.Destroyed)
                    AoOniCures.CureInfection(pawn, "unnatural healing");
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] unnatural healing patch failed: " + e);
            }
        }
    }

    // Luciferium: while the high is active, the oni infection cannot progress -
    // and an existing infection is purged. Mechanoid infections are unaffected
    // (mechs don't metabolize mechanites this way).
    [HarmonyPatch(typeof(Hediff), nameof(Hediff.Tick))]
    public static class Luciferium_AoOniPatch
    {
        public static void Postfix(Hediff __instance)
        {
            try
            {
                Pawn pawn = __instance.pawn;
                if (pawn == null || pawn.Dead || pawn.Destroyed)
                    return;
                if (__instance.def.defName != "LuciferiumHigh")
                    return;
                if (pawn.RaceProps.IsMechanoid)
                    return;

                var infection = Oni.GetInfection(pawn);
                if (infection == null)
                {
                    // No infection: nothing to do (immunity is implicit - the infection
                    // would be purged the moment it appears, see below)
                    return;
                }

                // Purge existing infection
                AoOniCures.CureInfection(pawn, "luciferium");
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] luciferium patch failed: " + e);
            }
        }
    }

    // New infections while on luciferium are purged the moment they are applied.
    [HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff), new[]
        { typeof(Hediff), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult) })]
    public static class HealthTracker_AoOniPatch
    {
        private static bool inPatch;

        public static void Postfix(Hediff hediff, Pawn ___pawn)
        {
            try
            {
                if (inPatch || hediff == null || ___pawn == null || ___pawn.Dead || ___pawn.Destroyed)
                    return;
                if (!(___pawn.health.hediffSet.GetFirstHediffOfDef(HediffDef.Named("LuciferiumHigh")) is Hediff_High luci) || luci.Severity < 0.3f)
                    return;
                if (!(___pawn.RaceProps.IsMechanoid))
                {
                    if (hediff is Hediff_AoOniInfection && !(hediff is Hediff_Doppelganger))
                    {
                        inPatch = true;
                        try
                        {
                            ___pawn.health.RemoveHediff(hediff);
                            Log.Message("[AoOni] " + ___pawn.LabelShortCap + "'s luciferium mechanites repelled an oni infection");
                        }
                        finally
                        {
                            inPatch = false;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                inPatch = false;
                Log.Error("[AoOni] health tracker patch failed: " + e);
            }
        }
    }


    // Colony mechs have a null skill tracker, but vanilla JobDriver_OperateScanner
    // calls pawn.skills.Learn unconditionally. Other mods (e.g. Pioneer) let worker
    // mechs scan, so guard the tick: replace the skills field load with a null-safe
    // getter and the Learn callvirt with a null-safe wrapper.
    [HarmonyPatch]
    public static class JobDriver_OperateScanner_AoOniPatch
    {
        public static Pawn_SkillTracker GetSkillsOrNull(Pawn pawn)
        {
            return pawn != null ? pawn.skills : null;
        }

        public static void LearnSafe(Pawn_SkillTracker tracker, SkillDef def, float xp, bool direct, bool ignoreLearnRate)
        {
            tracker?.Learn(def, xp, direct, ignoreLearnRate);
        }

        private static MethodBase TargetMethod()
        {
            var inner = AccessTools.Inner(typeof(JobDriver_OperateScanner), "<>c__DisplayClass1_0");
            var lambda = AccessTools.Method(inner, "<MakeNewToils>b__1");
            if (lambda != null)
                return lambda;
            // Fall back to MakeNewToils: the transpiler below no-ops on it.
            return AccessTools.Method(typeof(JobDriver_OperateScanner), "MakeNewToils");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            var skillsField = AccessTools.Field(typeof(Pawn), "skills");
            var learnMethod = AccessTools.Method(typeof(Pawn_SkillTracker), "Learn");
            var getSkills = AccessTools.Method(typeof(JobDriver_OperateScanner_AoOniPatch), nameof(GetSkillsOrNull));
            var learnSafe = AccessTools.Method(typeof(JobDriver_OperateScanner_AoOniPatch), nameof(LearnSafe));

            bool replacedField = false;
            for (int i = 0; i < codes.Count; i++)
            {
                if (!replacedField && codes[i].opcode == OpCodes.Ldfld && Equals(codes[i].operand, skillsField))
                {
                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = getSkills;
                    replacedField = true;
                }
                else if (replacedField && codes[i].opcode == OpCodes.Callvirt && Equals(codes[i].operand, learnMethod))
                {
                    codes[i].opcode = OpCodes.Call;
                    codes[i].operand = learnSafe;
                    break;
                }
            }
            return codes;
        }
    }

    // Register the game component
    [HarmonyPatch(typeof(Game), nameof(Game.InitNewGame))]
    public static class Game_InitNewGame_AoOniPatch
    {
        public static void Postfix(Game __instance) => GameCompRegistrar.AddComp(__instance);
    }

    [HarmonyPatch(typeof(Game), nameof(Game.LoadGame))]
    public static class Game_LoadGame_AoOniPatch
    {
        public static void Postfix(Game __instance) => GameCompRegistrar.AddComp(__instance);
    }

    public static class GameCompRegistrar
    {
        public static void AddComp(Game game)
        {
            if (game == null)
                return;
            if (!game.components.OfType<AoOniGameComp>().Any())
                game.components.Add(new AoOniGameComp(game));
        }
    }
}
