using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

    // Doppelganger: colonists returning from a caravan alone or as an unarmed pair
    // have a 20% chance of being replaced.
    [HarmonyPatch]
    public static class Caravan_AoOniDoppelPatch
    {
        public static bool prepared;

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

                var members = caravan.PawnsListForReading.Where(p => p.IsColonist && p.RaceProps.Humanlike && !p.Destroyed).ToList();
                if (members.Count == 0 || members.Count > 2)
                    return;
                if (members.Count == 2 && members.Any(m => m.equipment?.Primary != null))
                    return;

                if (!Rand.Chance(0.20f))
                    return;

                var victim = members.RandomElement();
                victim.health.AddHediff(HediffMaker.MakeHediff(HediffDefOf_AoOni.AoOniDoppelganger, victim));
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] doppelganger patch failed: " + e);
            }
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
                var test = DefDatabase<RecipeDef>.GetNamed("AoOniInfectionTest", false);
                var cure = DefDatabase<RecipeDef>.GetNamed("AoOniCure", false);
                if (test == null || cure == null)
                    return;

                int n = 0;
                foreach (var def in DefDatabase<ThingDef>.AllDefs)
                {
                    if (def.race == null)
                        continue;
                    n++;
                    if (def.recipes == null)
                        def.recipes = new List<RecipeDef>();
                    if (!def.recipes.Contains(test))
                        def.recipes.Add(test);
                    if (!def.recipes.Contains(cure))
                        def.recipes.Add(cure);
                    AccessTools.Field(typeof(ThingDef), "allRecipesCached")?.SetValue(def, null);
                }
                Log.Message($"[AoOni] registered infection test/cure surgeries on {n} pawn races");
            }
            catch (Exception e)
            {
                Log.Error("[AoOni] surgery registration failed: " + e);
            }
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
