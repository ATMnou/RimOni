using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace AoOni
{
    public class AoOniGameComp : GameComponent
    {
        private class OniState
        {
            public int specialNext;      // generic 2500-tick abilities
            public int spiderNext;       // 10s web
            public int amoebaNext;
            public int batNext;
            public Pawn batTarget;
            public int fuwattyPhase;     // 0 moving, 1 paused-waiting-for-dash
            public int fuwattyDashAt;
            public float fuwattyDist;    // cells moved since last dash
            public IntVec3 lastPos = IntVec3.Invalid;
        }

        private int ticks;
        private static int ticksStatic;
        private Sustainer musicSustainer;
        private static readonly Dictionary<Thing, OniState> states = new Dictionary<Thing, OniState>();

        private class GasZone
        {
            public IntVec3 center;
            public float radius;
            public int expiryTick;
        }

        private static readonly List<GasZone> gasZones = new List<GasZone>();

        public AoOniGameComp(Game game)
        {
            gasZones.Clear();
        }

        // ============ Blueberry gas (dying plumpy oni) ============

        public static void SpawnBerryGas(Map map, IntVec3 cell)
        {
            gasZones.Add(new GasZone { center = cell, radius = 4.5f, expiryTick = Find.TickManager.TicksGame + 1800 });

            var moteDef = DefDatabase<ThingDef>.GetNamed("Mote_AoOniGas", false);
            if (moteDef == null)
                return;

            for (int i = 0; i < 10; i++)
            {
                var cell2 = cell.RandomAdjacentCell8Way();
                for (int j = 0; j < 3; j++)
                    cell2 = cell2.RandomAdjacentCell8Way();
                if (!cell2.InBounds(map) || !cell2.Walkable(map))
                    continue;

                var mote = (MoteThrown)ThingMaker.MakeThing(moteDef);
                mote.exactPosition = cell2.ToVector3Shifted() + new Vector3(Rand.Range(-0.3f, 0.3f), 0f, Rand.Range(-0.3f, 0.3f));
                float ang = Rand.Range(0f, 360f) * Mathf.Deg2Rad;
                mote.Velocity = new Vector3(Mathf.Sin(ang), 0f, Mathf.Cos(ang)) * Rand.Range(0.08f, 0.3f);
                GenSpawn.Spawn(mote, cell2, map, WipeMode.Vanish);
            }
        }

        private static void GasTick()
        {
            if (gasZones.Count == 0)
                return;

            int now = Find.TickManager.TicksGame;
            for (int i = gasZones.Count - 1; i >= 0; i--)
            {
                if (gasZones[i].expiryTick <= now)
                {
                    gasZones.RemoveAt(i);
                    continue;
                }

                foreach (var map in Current.Game.Maps)
                {
                    foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                    {
                        if (pawn.Dead || pawn.Downed || Oni.IsOni(pawn))
                            continue;
                        if (pawn.Position.InHorDistOf(gasZones[i].center, gasZones[i].radius))
                            ApplyBerryGas(pawn, 0.10f);
                    }
                }
            }
        }

        private static void ApplyBerryGas(Pawn pawn, float amount)
        {
            var def = DefDatabase<HediffDef>.GetNamed("AoOniBerryGas", false);
            if (def == null)
                return;
            var hediff = pawn.health.hediffSet.GetFirstHediffOfDef(def);
            if (hediff == null)
            {
                hediff = HediffMaker.MakeHediff(def, pawn);
                pawn.health.AddHediff(hediff);
            }
            hediff.Severity = Mathf.Min(1f, hediff.Severity + amount);
        }

        // Severe blueberry gas exposure can develop into an Oni infection
        // with the same probability curve as lung rot.
        private static void BerryInfectionTick()
        {
            var gasDef = DefDatabase<HediffDef>.GetNamed("AoOniBerryGas", false);
            var infectionDef = DefDatabase<HediffDef>.GetNamed("AoOniInfection", false);
            if (gasDef == null || infectionDef == null)
                return;

            foreach (var map in Current.Game.Maps)
            {
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (pawn.Dead || pawn.Downed || Oni.IsOni(pawn))
                        continue;
                    var gas = pawn.health.hediffSet.GetFirstHediffOfDef(gasDef);
                    if (gas == null || gas.Severity < 0.5f)
                        continue;

                    // vanilla lung rot MTB curve: (0.5 -> 8 days) .. (1.0 -> 0.5 days)
                    float mtbDays = 8f - 7.5f * Mathf.Clamp01((gas.Severity - 0.5f) / 0.5f);
                    if (Rand.MTBEventOccurs(mtbDays, GenDate.TicksPerDay, 600) && Oni.GetInfection(pawn) == null)
                    {
                        pawn.health.AddHediff(HediffMaker.MakeHediff(infectionDef, pawn));
                        Log.Message("[AoOni] " + pawn.LabelShortCap + " infected by blueberry gas exposure");
                    }
                }
            }
        }

        // ============ Hundred eyes oni: shared vision + invisibility detection ============

        private static void HundredEyeTick()
        {
            foreach (var map in Current.Game.Maps)
            {
                var pawns = map.mapPawns.AllPawnsSpawned;
                foreach (var eye in pawns)
                {
                    if (eye.def.defName != "AoOni_HundredEyeOni" || eye.Downed || eye.Dead)
                        continue;

                    // Reveal psychologically invisible pawns (sightstealers, revenants...)
                    foreach (var other in pawns)
                    {
                        if (other == eye || other.Dead || !other.Spawned)
                            continue;
                        if (!InvisibilityUtility.IsPsychologicallyInvisible(other))
                            continue;
                        if (other.Position.InHorDistOf(eye.Position, 35f) &&
                            GenSight.LineOfSight(eye.Position, other.Position, map))
                        {
                            other.GetInvisibilityComp()?.BecomeVisible(true);
                        }
                    }

                    // Share its sight with other onis
                    var target = eye.mindState?.enemyTarget;
                    if (target != null && !target.Destroyed)
                    {
                        foreach (var other in pawns)
                        {
                            if (other == eye || !Oni.IsOni(other) || other.Downed || other.Dead)
                                continue;
                            if (other.mindState != null && other.mindState.enemyTarget == null &&
                                other.Position.InHorDistOf(eye.Position, 60f))
                            {
                                other.mindState.enemyTarget = target;
                            }
                        }
                    }
                }
            }
        }

        // ============ Bat oni: hunts wild animals in peaceful times ============

        private static void BatTick()
        {
            foreach (var map in Current.Game.Maps)
            {
                var pawns = map.mapPawns.AllPawnsSpawned;
                foreach (var bat in pawns)
                {
                    if (bat.def.defName != "AoOni_BatOni" || bat.Downed || bat.Dead || bat.Destroyed)
                        continue;

                    var st = GetState(bat);

                    // Skip while fighting
                    var enemy = bat.mindState?.enemyTarget;
                    if (enemy != null && !enemy.Destroyed)
                        continue;

                    if (st.batTarget == null)
                    {
                        if (ticksStatic < st.batNext)
                            continue;
                        st.batNext = ticksStatic + 2400;
                        if (!Rand.Chance(0.30f))
                            continue;

                        Pawn prey = null;
                        float best = float.MaxValue;
                        foreach (var other in pawns)
                        {
                            if (other == bat || other.Downed || other.Dead)
                                continue;
                            if (!other.RaceProps.Animal || other.Faction != null)
                                continue;
                            float d = other.Position.DistanceToSquared(bat.Position);
                            if (d < best && d <= 40f * 40f)
                            {
                                best = d;
                                prey = other;
                            }
                        }
                        if (prey == null)
                            continue;
                        st.batTarget = prey;
                    }

                    var prey2 = st.batTarget;
                    if (prey2 == null || prey2.Dead || prey2.Destroyed || !prey2.Spawned)
                    {
                        st.batTarget = null;
                        continue;
                    }

                    if (bat.Position.AdjacentTo(prey2.Position))
                    {
                        prey2.TakeDamage(new DamageInfo(DamageDefOf.Bite, Rand.Range(8, 12), 0.3f, -1f, bat));
                        if (!prey2.Dead && !prey2.Destroyed)
                        {
                            var fastDef = DefDatabase<HediffDef>.GetNamed("AoOniInfectionFast", false);
                            if (fastDef != null && Oni.GetInfection(prey2) == null)
                                prey2.health.AddHediff(HediffMaker.MakeHediff(fastDef, prey2));
                        }
                        st.batTarget = null;
                    }
                    else if (bat.Position.InHorDistOf(prey2.Position, 45f))
                    {
                        // keep chasing
                        var cell = CellFinder.RandomClosewalkCellNear(prey2.Position, map, 1);
                        if (cell.IsValid)
                        {
                            var job = JobMaker.MakeJob(JobDefOf.Goto, cell);
                            job.expiryInterval = 600;
                            job.checkOverrideOnExpire = true;
                            bat.jobs.TryTakeOrderedJob(job, Verse.AI.JobTag.Misc);
                        }
                    }
                    else
                    {
                        st.batTarget = null;
                    }
                }
            }
        }

        // thingIDNumbers of meals cooked by doppelgangers
        public static HashSet<int> taintedThings = new HashSet<int>();

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look(ref taintedThings, "taintedThings", LookMode.Value);
            if (taintedThings == null)
                taintedThings = new HashSet<int>();
        }

        public override void GameComponentTick()
        {
            ticks++;
            ticksStatic = ticks;
            if (ticks % 30 == 0)
                FuwattyTick();
            if (ticks % 60 == 0)
                AmoebaTick();
            if (ticks % 600 == 0)
                BerryInfectionTick();
            if (ticks % 250 != 0)
            {
                GasTick();
                HundredEyeTick();
                BatTick();
                return;
            }
            GasTick();
            HundredEyeTick();
            BatTick();

            foreach (var map in Current.Game.Maps)
            {
                var pawns = map.mapPawns.AllPawnsSpawned.ToList();
                foreach (var pawn in pawns)
                {
                    if (pawn.Dead || pawn.Destroyed)
                        continue;

                    if (Oni.IsOni(pawn))
                        TickOni(pawn, map);

                    var infection = Oni.GetInfection(pawn);
                    if (infection != null)
                    {
                        if (infection.ageTicks >= infection.TransformTicks || infection.Severity >= 1f)
                        {
                            Transformer.TransformPawn(pawn);
                            continue;
                        }
                        if (infection.ageTicks >= infection.TransformTicks - 10000 &&
                            pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf_AoOni.AoOniChanging) == null)
                        {
                            pawn.health.AddHediff(HediffMaker.MakeHediff(HediffDefOf_AoOni.AoOniChanging, pawn));
                        }
                    }

                    var doppel = pawn.health?.hediffSet?.GetFirstHediffOfDef(HediffDefOf_AoOni.AoOniDoppelganger) as Hediff_Doppelganger;
                    if (doppel != null && !pawn.Destroyed && (pawn.Downed || (pawn.needs?.mood != null && pawn.needs.mood.CurLevel <= 0.10f)))
                        Transformer.TransformPawn(pawn);

                    // Zombie oni slows down in freezing temperatures
                    if (pawn.def.defName == "AoOni_ZombieOni" && map.mapTemperature.OutdoorTemp < 0f)
                    {
                        if (pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf_AoOni.AoOniZombieCold) == null)
                        {
                            var hd = HediffMaker.MakeHediff(HediffDefOf_AoOni.AoOniZombieCold, pawn);
                            pawn.health.AddHediff(hd);
                        }
                    }
                }
            }

            UpdateMusic();
        }

        private void TickOni(Pawn pawn, Map map)
        {
            if (pawn.Downed || pawn.InContainerEnclosed)
                return;

            var st = GetState(pawn);
            string def = pawn.def.defName;

            // 4th dimension oni: teleport in front of a random colonist
            if (def == "AoOni_FourthDimensionOni" && ticks >= st.specialNext)
            {
                st.specialNext = ticks + 2500;
                if (Rand.Chance(0.20f))
                {
                    var colonists = map.mapPawns.FreeColonistsSpawned.Where(c => !c.Downed).ToList();
                    if (colonists.Count > 0)
                    {
                        var target = colonists.RandomElement();
                        var cell = CellFinder.RandomClosewalkCellNear(target.Position, map, 2);
                        if (cell.IsValid)
                        {
                            pawn.Position = cell;
                            pawn.Notify_Teleported(true);
                            pawn.stances.stunner.StunFor(300, null);
                        }
                    }
                }
            }

            // Clown oni: self-destruct
            if (def == "AoOni_ClownOni" && ticks >= st.specialNext)
            {
                st.specialNext = ticks + 2500;
                bool downed = pawn.Downed;
                bool contained = pawn.GetComp<CompHoldingPlatformTarget>()?.CurrentlyHeldOnPlatform ?? false;
                if ((downed || !contained) && Rand.Chance(0.05f))
                {
                    GenExplosion.DoExplosion(pawn.Position, map, 3.5f, DamageDefOf.Bomb, pawn, 80, 0.3f);
                    pawn.Kill(new DamageInfo(DamageDefOf.Bomb, 999));
                }
            }

            // Inured oni: self-injury
            if (def == "AoOni_InuredOni" && ticks >= st.specialNext)
            {
                st.specialNext = ticks + 2500;
                var parts = pawn.health.hediffSet.GetNotMissingParts();
                if (parts.Any())
                {
                    var part = parts.RandomElementByWeight(p => p.coverageAbs);
                    pawn.TakeDamage(new DamageInfo(DamageDefOf.Cut, Rand.Range(8, 16), 0.1f, -1f, null, part));
                }
            }

            // Monstrous oni: spawn an Ao Oni nearby
            if (def == "AoOni_MonstrousOni" && ticks >= st.specialNext)
            {
                st.specialNext = ticks + 2500;
                if (Rand.Chance(0.20f))
                {
                    string variant = Transformer.PickVariant(map, animalOrigin: false, mechanoidOrigin: false);
                    Transformer.SpawnOni(map, pawn.Position, variant, 1);
                }
            }

            // Spider oni: web nearest enemy
            if (def == "AoOni_SpiderOni" && ticks >= st.spiderNext)
            {
                st.spiderNext = ticks + 600;
                Pawn best = null;
                float bestDist = float.MaxValue;
                foreach (var other in map.mapPawns.AllPawnsSpawned)
                {
                    if (other == pawn || other.Downed || other.Dead) continue;
                    if (!other.HostileTo(pawn)) continue;
                    float d = other.Position.DistanceToSquared(pawn.Position);
                    if (d < bestDist && d <= 30f * 30f) { bestDist = d; best = other; }
                }
                if (best != null && best.health.hediffSet.GetFirstHediffOfDef(HediffDefOf_AoOni.AoOniWeb) == null)
                {
                    var hd = HediffMaker.MakeHediff(HediffDefOf_AoOni.AoOniWeb, best);
                    best.health.AddHediff(hd);
                }
            }

        }

        // Fuwatty: every 8 cells of movement, pause 1 second, then dash 6 cells in a straight line
        private void FuwattyTick()
        {
            foreach (var map in Current.Game.Maps)
            {
                var pawns = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    var pawn = pawns[i];
                    if (pawn.def.defName != "AoOni_Fuwatty" || pawn.Downed || pawn.Dead || pawn.Destroyed)
                        continue;

                    var st = GetState(pawn);

                    if (st.fuwattyPhase == 0)
                    {
                        if (st.lastPos.IsValid && pawn.Position != st.lastPos)
                            st.fuwattyDist += pawn.Position.DistanceTo(st.lastPos);
                        st.lastPos = pawn.Position;

                        if (st.fuwattyDist >= 8f)
                        {
                            st.fuwattyDist = 0f;
                            st.fuwattyPhase = 1;
                            st.fuwattyDashAt = ticks + 60;
                            pawn.stances.stunner.StunFor(60, null);
                        }
                    }
                    else if (ticks >= st.fuwattyDashAt)
                    {
                        st.fuwattyPhase = 0;
                        st.lastPos = IntVec3.Invalid;
                        DashForward(pawn, map);
                    }
                }
            }
        }

        // Amoeba oni: fire oni bolts at range and keep its distance from melee threats
        private void AmoebaTick()
        {
            foreach (var map in Current.Game.Maps)
            {
                var pawns = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    var pawn = pawns[i];
                    if (pawn.def.defName != "AoOni_AmoebaOni" || pawn.Downed || pawn.Dead || pawn.Destroyed)
                        continue;

                    var st = GetState(pawn);

                    var target = pawn.mindState?.enemyTarget as Pawn;
                    if (target == null || !target.Spawned || target.Dead || !pawn.HostileTo(target))
                    {
                        // Acquire the nearest hostile pawn ourselves
                        float best = float.MaxValue;
                        foreach (var other in map.mapPawns.AllPawnsSpawned)
                        {
                            if (other.Downed || other.Dead || !pawn.HostileTo(other))
                                continue;
                            float d = other.Position.DistanceToSquared(pawn.Position);
                            if (d < best && d <= 25f * 25f)
                            {
                                best = d;
                                target = other;
                            }
                        }
                        if (target == null)
                            continue;
                    }

                    float dist = pawn.Position.DistanceTo(target.Position);

                    // Keep away from melee range
                    if (dist < 5f)
                    {
                        TryRetreat(pawn, target);
                        continue;
                    }

                    // Fire an oni bolt directly
                    if (ticks >= st.amoebaNext && dist <= 20f &&
                        GenSight.LineOfSight(pawn.Position, target.Position, map))
                    {
                        st.amoebaNext = ticks + Rand.Range(180, 240);
                        var projectile = (Projectile)ThingMaker.MakeThing(ThingDefOf_AoOni.ProjectileOniBolt);
                        GenSpawn.Spawn(projectile, pawn.Position + pawn.Rotation.FacingCell, map);
                        projectile.Launch(pawn, pawn.TrueCenter(), target, target,
                            ProjectileHitFlags.IntendedTarget, false);
                    }
                }
            }
        }

        private void TryRetreat(Pawn pawn, Pawn threat)
        {
            IntVec3 away = pawn.Position - threat.Position;
            IntVec3 dir = new IntVec3(Mathf.Clamp(away.x, -1, 1), 0, Mathf.Clamp(away.z, -1, 1));
            Map map = pawn.Map;

            for (int k = 5; k >= 3; k--)
            {
                IntVec3 cell = pawn.Position + dir * k;
                if (cell.InBounds(map) && cell.Standable(map) && cell.Walkable(map) && pawn.CanReach(cell, Verse.AI.PathEndMode.OnCell, Danger.Deadly))
                {
                    var job = JobMaker.MakeJob(JobDefOf.Goto, cell);
                    job.expiryInterval = 250;
                    job.checkOverrideOnExpire = true;
                    pawn.jobs.TryTakeOrderedJob(job, Verse.AI.JobTag.Misc);
                    return;
                }
            }
        }

        private void DashForward(Pawn pawn, Map map)
        {
            // Only dash while in combat
            var target = pawn.mindState?.enemyTarget as Pawn;
            if (target == null || !target.Spawned || target.Dead || !pawn.HostileTo(target))
            {
                pawn.stances.stunner.StunFor(0, null);
                return;
            }

            IntVec3 normalized = new IntVec3(Mathf.Clamp(target.Position.x - pawn.Position.x, -1, 1), 0,
                                             Mathf.Clamp(target.Position.z - pawn.Position.z, -1, 1));
            if (normalized == IntVec3.Zero)
                normalized = pawn.Rotation.FacingCell;
            if (normalized == IntVec3.Zero)
                return;

            IntVec3 dest = pawn.Position;
            var hit = new List<Pawn>();
            for (int step = 1; step <= 6; step++)
            {
                IntVec3 cell = pawn.Position + normalized * step;
                if (!cell.InBounds(map) || !cell.Walkable(map))
                    break;
                dest = cell;

                // Scratch everything touching the dash path
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        var c = cell + new IntVec3(dx, 0, dz);
                        if (!c.InBounds(map))
                            continue;
                        var victims = map.thingGrid.ThingsListAt(c);
                        for (int t = 0; t < victims.Count; t++)
                        {
                            if (victims[t] is Pawn v && !v.Dead && !v.Destroyed && !hit.Contains(v))
                                hit.Add(v);
                        }
                    }
                }
            }

            if (dest == pawn.Position)
                return;

            pawn.Position = dest;
            pawn.Notify_Teleported(true);
            foreach (var v in hit)
            {
                if (v.Dead || v.Destroyed)
                    continue;
                v.TakeDamage(new DamageInfo(DamageDefOf.Scratch, Rand.Range(16, 22), 0.2f, -1f, pawn));
            }
        }

        private static OniState GetState(Thing pawn)
        {
            if (!states.TryGetValue(pawn, out var st))
            {
                st = new OniState { specialNext = ticksStatic + Rand.Range(0, 2000), spiderNext = ticksStatic + 600, amoebaNext = ticksStatic + 300 };
                states[pawn] = st;
            }
            return st;
        }

        private void UpdateMusic()
        {
            bool anyActive = false;
            foreach (var map in Current.Game.Maps)
            {
                foreach (var pawn in map.mapPawns.AllPawnsSpawned)
                {
                    if (!Oni.IsOni(pawn)) continue;
                    if (pawn.Downed) continue;
                    if (pawn.GetComp<CompHoldingPlatformTarget>()?.CurrentlyHeldOnPlatform ?? false) continue;
                    anyActive = true;
                    break;
                }
                if (anyActive) break;
            }

            if (anyActive)
            {
                // Keep vanilla (Anomaly) music silent while the horror theme plays.
                // preventDangerTransition is required, otherwise the danger-music
                // transition overrides our silence window while the oni is attacking.
                Find.MusicManagerPlay.ForceFadeoutAndSilenceFor(8f, 2f, true);
            }

            if (anyActive && (musicSustainer == null || musicSustainer.Ended))
            {
                var map = Current.Game.Maps.FirstOrDefault();
                if (map != null)
                {
                    var cell = CellFinder.RandomClosewalkCellNear(map.Center, map, 5);
                    var info = SoundInfo.InMap(new TargetInfo(cell.IsValid ? cell : map.Center, map), MaintenanceType.PerTick);
                    musicSustainer = SoundDefOf_AoOni.AoOniHorrorMusic.TrySpawnSustainer(info);
                }
            }
            else if (!anyActive && musicSustainer != null && !musicSustainer.Ended)
            {
                musicSustainer.End();
                musicSustainer = null;
            }
        }
    }

    public static class HediffDefOf_AoOni
    {
        public static HediffDef AoOniInfection => DefDatabase<HediffDef>.GetNamed("AoOniInfection", true);
        public static HediffDef AoOniChanging => DefDatabase<HediffDef>.GetNamed("AoOniChanging", true);
        public static HediffDef AoOniInfectionZombie => DefDatabase<HediffDef>.GetNamed("AoOniInfectionZombie", true);
        public static HediffDef AoOniDoppelganger => DefDatabase<HediffDef>.GetNamed("AoOniDoppelganger", true);
        public static HediffDef AoOniWeb => DefDatabase<HediffDef>.GetNamed("AoOniWeb", true);
        public static HediffDef AoOniDash => DefDatabase<HediffDef>.GetNamed("AoOniDash", true);
        public static HediffDef AoOniZombieCold => DefDatabase<HediffDef>.GetNamed("AoOniZombieCold", true);
    }

    public static class ThingDefOf_AoOni
    {
        public static ThingDef ProjectileOniBolt => DefDatabase<ThingDef>.GetNamed("Projectile_OniBolt", true);
    }

    public static class SoundDefOf_AoOni
    {
        public static SoundDef AoOniHorrorMusic => DefDatabase<SoundDef>.GetNamed("AoOni_HorrorMusic", true);
    }
}
