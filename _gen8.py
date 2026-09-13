# -*- coding: utf-8 -*-
import os
os.chdir(r"D:\SteamLibrary\steamapps\common\RimWorld\Mods\Aooni")

# ============ Patches.cs: doppelganger contamination (cooking / surgery) ============
p = "Source/AoOni/Patches.cs"
s = open(p, encoding="utf-8").read()

contamination = '''
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

'''
marker = "    // Register the game component"
assert marker in s
s = s.replace(marker, contamination + marker, 1)
open(p, "w", encoding="utf-8").write(s)
print("patches ok")

# ============ GameComp: tainted set + save ============
p = "Source/AoOni/AoOniGameComp.cs"
s = open(p, encoding="utf-8").read()
old = '''        private static readonly List<GasZone> gasZones = new List<GasZone>();

        public AoOniGameComp(Game game)
        {
            gasZones.Clear();
        }

        public override void ExposeData() { }'''
new = '''        private static readonly List<GasZone> gasZones = new List<GasZone>();

        // thingIDNumbers of meals cooked by doppelgangers
        public static HashSet<int> taintedThings = new HashSet<int>();

        public AoOniGameComp(Game game)
        {
            gasZones.Clear();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref taintedThings, "taintedThings", LookMode.Value);
            if (taintedThings == null)
                taintedThings = new HashSet<int>();
        }'''
assert old in s, "taint state"
s = s.replace(old, new)
open(p, "w", encoding="utf-8").write(s)
print("comp ok")
