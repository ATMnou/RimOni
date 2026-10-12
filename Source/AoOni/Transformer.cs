using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace AoOni
{
    public static class Oni
    {
        public const string DefPrefix = "AoOni_";

        public static bool IsOni(Pawn pawn)
        {
            return pawn != null && pawn.def != null && pawn.def.defName != null
                && pawn.def.defName.StartsWith(DefPrefix)
                && pawn.def.defName != "AoOni_Nucleus";
        }

        public static Hediff_AoOniInfection GetInfection(Pawn pawn)
        {
            return pawn?.health?.hediffSet?.hediffs
                ?.OfType<Hediff_AoOniInfection>().FirstOrDefault();
        }

        // True when an oni infection has been revealed by the surgical inspection test
        public static bool HasDiagnosedInfection(Pawn pawn)
        {
            return pawn?.health?.hediffSet?.hediffs
                ?.OfType<Hediff_AoOniInfection>().Any(h => h.Diagnosed) ?? false;
        }

        public static Hediff_Doppelganger GetDoppelganger(Pawn pawn)
        {
            return pawn?.health?.hediffSet?.GetFirstHediffOfDef(HediffDefOf_AoOni.AoOniDoppelganger) as Hediff_Doppelganger;
        }

        // Codex: track which oni variants the colony has personally encountered
        private static readonly HashSet<string> seenVariants = new HashSet<string>();

        public static void MarkVariantSeen(string raceDefName)
        {
            if (raceDefName == null || !seenVariants.Add(raceDefName))
                return;
            try
            {
                var entry = DefDatabase<EntityCodexEntryDef>.GetNamed("AoOni", false);
                if (entry == null)
                    return;

                // First sighting: reveal the entry (this also unlocks its category),
                // but only after the "onionomy" research - the codex page shows the
                // variants encountered so far.
                var basics = DefDatabase<ResearchProjectDef>.GetNamed("AoOniBasics", false);
                bool researched = basics != null && basics.IsFinished;
                if (!codexRevealed && researched)
                {
                    Find.EntityCodex.SetDiscovered(entry);
                    // The page links the base Ao Oni thing - register it so the
                    // codex list doesn't show it as "undiscovered"
                    var baseThing = DefDatabase<ThingDef>.GetNamed("AoOni_AoOni", false);
                    if (baseThing != null)
                        Find.EntityCodex.SetDiscovered(entry, baseThing);
                    codexRevealed = true;
                    Log.Message("[AoOni] entity codex page revealed (onionomy complete)");
                }

                // Track the encountered variant thing for the codex "discovered entities" list
                var thingDef = DefDatabase<ThingDef>.GetNamed(raceDefName, false);
                if (thingDef != null && Find.EntityCodex.Discovered(entry))
                {
                    Find.EntityCodex.SetDiscovered(entry, thingDef);
                    // Show encountered variants on the page itself
                    if (entry.linkedThings == null)
                        entry.linkedThings = new List<ThingDef>();
                    if (!entry.linkedThings.Contains(thingDef))
                        entry.linkedThings.Add(thingDef);
                }

                // Persist what we've seen so it survives a save/reload
                if (AoOniGameComp.Instance != null && !AoOniGameComp.Instance.seenVariantsSaved.Contains(raceDefName))
                    AoOniGameComp.Instance.seenVariantsSaved.Add(raceDefName);

                if (!researched)
                    pendingReveal = true;
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] codex discovery failed: " + e);
            }
        }

        // Set once the codex page has been revealed
        private static bool codexRevealed;

        // An oni was seen before the onionomy research; reveal as soon as it finishes
        private static bool pendingReveal;

        /// <summary>Call when the onionomy research completes: reveals the codex page.</summary>
        public static void OnBasicsResearched()
        {
            if (codexRevealed || !pendingReveal)
                return;
            codexRevealed = true;
            pendingReveal = false;
            var entry = DefDatabase<EntityCodexEntryDef>.GetNamed("AoOni", false);
            if (entry != null)
            {
                Find.EntityCodex.SetDiscovered(entry);
                var baseThing = DefDatabase<ThingDef>.GetNamed("AoOni_AoOni", false);
                if (baseThing != null)
                    Find.EntityCodex.SetDiscovered(entry, baseThing);
                Log.Message("[AoOni] entity codex page revealed (retroactive)");
            }
        }

        // All oni events assume the void monolith at level 1+
        public static bool MonolithActive()
        {
            return !ModsConfig.AnomalyActive || Find.Anomaly.HighestLevelReached >= 1;
        }

        public static void ApplyInfection(Pawn victim, Pawn attacker)
        {
            if (victim == null || victim.Dead || victim.Destroyed)
                return;
            if (!MonolithActive())
                return;
            if (IsOni(victim))
                return;
            if (GetInfection(victim) != null)
                return;

            // Mechanoid infection can be disabled in mod settings
            if (victim.RaceProps.IsMechanoid)
            {
                if (AoOniMod.settings == null || !AoOniMod.settings.mechInfection)
                    return;
            }

            bool zombie = attacker != null && attacker.def.defName == "AoOni_ZombieOni";
            if (!zombie && !Rand.Chance(0.70f))
                return;
            Log.Message("[AoOni] " + victim.LabelShortCap + " infected by " + (attacker != null ? attacker.def.defName : "?") + (zombie ? " (zombie, guaranteed)" : ""));

            HediffDef def = zombie ? HediffDefOf_AoOni.AoOniInfectionZombie : HediffDefOf_AoOni.AoOniInfection;
            var hediff = HediffMaker.MakeHediff(def, victim);
            victim.health.AddHediff(hediff);
        }

        public static Faction OniFaction()
        {
            var fac = Find.FactionManager.FirstFactionOfDef(FactionDefOf.Entities);
            if (fac == null)
                fac = FactionUtility.DefaultFactionFrom(FactionDefOf.Entities);
            return fac;
        }
    }

    public static class Transformer
    {
        // General variants (excluding the base Ao Oni)
        private static readonly string[] GeneralVariants =
        {
            "AoOni_FourthDimensionOni", "AoOni_AmoebaOni", "AoOni_ClownOni", "AoOni_EggPlantOni",
            "AoOni_FlatOni", "AoOni_Fuwatty", "AoOni_HunchbackOni", "AoOni_IceOni", "AoOni_InuredOni",
            "AoOni_HundredEyeOni", "AoOni_MonstrousOni", "AoOni_PlumpyOni", "AoOni_SixArmsOni",
            "AoOni_SkinnyOni", "AoOni_SquattoOni", "AoOni_ZombieOni"
        };

        private static readonly string[] AnimalVariants =
        {
            "AoOni_BatOni", "AoOni_BlueShrimp", "AoOni_FlyingOni",
            "AoOni_JellyfishOni", "AoOni_SlugOni", "AoOni_SpiderOni"
        };

        public static string PickVariant(Map map, bool animalOrigin, bool mechanoidOrigin)
        {
            if (mechanoidOrigin)
                return "AoOni_PuppetOni";

            bool cold = map != null && map.mapTemperature.OutdoorTemp < 0f;

            var pool = new List<(string def, float w)>();

            if (animalOrigin)
            {
                foreach (var v in AnimalVariants)
                    pool.Add((v, 4f));
                foreach (var v in GeneralVariants)
                    pool.Add((v, 0.6f));
            }
            else
            {
                float baseW = AoOniMod.settings?.baseOniChance ?? 0.30f;
                pool.Add(("AoOni_AoOni", baseW));
                float restW = (1f - baseW) / GeneralVariants.Length;
                foreach (var v in GeneralVariants)
                    pool.Add((v, restW));
                foreach (var v in AnimalVariants)
                    pool.Add((v, 0.3f));
            }

            if (cold)
            {
                for (int i = 0; i < pool.Count; i++)
                {
                    if (pool[i].def == "AoOni_IceOni") pool[i] = (pool[i].def, pool[i].w + 5f);
                    if (pool[i].def == "AoOni_ZombieOni") pool[i] = (pool[i].def, pool[i].w + 2f);
                }
            }

            float total = pool.Sum(p => p.w);
            float roll = Rand.Range(0f, total);
            float acc = 0f;
            foreach (var p in pool)
            {
                acc += p.w;
                if (roll <= acc)
                    return p.def;
            }
            return "AoOni_AoOni";
        }

        public static void SpawnOni(Map map, IntVec3 cell, string raceDefName, int count)
        {
            var kind = DefDatabase<PawnKindDef>.GetNamed(raceDefName, false);
            if (kind == null)
            {
                Log.Error("[AoOni] SpawnOni: unknown pawn kind " + raceDefName);
                return;
            }

            var fac = Faction.OfEntities;
            if (fac == null)
            {
                Log.Error("[AoOni] SpawnOni: Entities faction not found");
                return;
            }

            for (int i = 0; i < count; i++)
            {
                var request = new PawnGenerationRequest(kind, fac, PawnGenerationContext.NonPlayer, map.Tile);
                var pawn = PawnGenerator.GeneratePawn(request);
                var spawnCell = CellFinder.RandomClosewalkCellNear(cell, map, 3);
                GenSpawn.Spawn(pawn, spawnCell.IsValid ? spawnCell : cell, map, Rot4.Random, WipeMode.Vanish, false);
            }

            if (count > 0)
                Oni.MarkVariantSeen(raceDefName);
        }

        public static void TransformPawn(Pawn victim)
        {
            try
            {
                Map map = victim.Map;
                IntVec3 cell = victim.Position;
                string name = victim.LabelShortCap;

                if (map == null)
                {
                    map = Current.Game.Maps.FirstOrDefault(m => m.IsPlayerHome);
                    if (map == null) return;
                    cell = map.Center;
                }

                bool animalOrigin = victim.RaceProps.Animal;
                bool mechanoidOrigin = victim.RaceProps.IsMechanoid;
                string variant = PickVariant(map, animalOrigin, mechanoidOrigin);

                Log.Message("[AoOni] " + name + " is transforming into " + variant);

                // Remove the victim without leaving a corpse
                victim.Destroy();

                SpawnOni(map, cell, variant, 1);
                Messages.Message("AoOni_Transformed".Translate(name, DefDatabase<ThingDef>.GetNamed(variant, true).label),
                    new TargetInfo(cell, map), MessageTypeDefOf.ThreatBig, false);
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] TransformPawn failed: " + e);
            }
        }
    }
}
