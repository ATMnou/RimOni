using UnityEngine;
using Verse;
using HarmonyLib;

namespace AoOni
{
    public class AoOniMod : Mod
    {
        public static AoOniSettings settings;

        public AoOniMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<AoOniSettings>();
            new Harmony("ATMnou.aooni").PatchAll();
        }

        public override string SettingsCategory() => "Ao Oni";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var ls = new Listing_Standard();
            ls.Begin(inRect);

            ls.Label("Ao Oni".Translate() + " (" + (settings.baseOniChance * 100f).ToString("0") + "%): " +
                     "AoOni_BaseOniChance".Translate());
            settings.baseOniChance = ls.Slider(settings.baseOniChance, 0.05f, 0.8f);
            ls.Gap(8f);

            ls.CheckboxLabeled("AoOni_MechInfection".Translate(), ref settings.mechInfection,
                "AoOni_MechInfectionTip".Translate());
            ls.Gap(4f);
            ls.CheckboxLabeled("AoOni_DoppelgangerJoin".Translate(), ref settings.doppelgangerJoin,
                "AoOni_DoppelgangerJoinTip".Translate());

            ls.End();
            base.DoSettingsWindowContents(inRect);
        }
    }

    public class AoOniSettings : ModSettings
    {
        public float baseOniChance = 0.30f;
        public bool mechInfection = true;
        public bool doppelgangerJoin = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref baseOniChance, "baseOniChance", 0.30f);
            Scribe_Values.Look(ref mechInfection, "mechInfection", true);
            Scribe_Values.Look(ref doppelgangerJoin, "doppelgangerJoin", true);
        }
    }
}
