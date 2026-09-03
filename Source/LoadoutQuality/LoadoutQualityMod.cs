using System;
using System.Collections.Generic;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace LoadoutQuality
{
    public class LQSettings : ModSettings
    {
        // Install-as-consent: the upgrade behaviour ships ON — installing the mod is
        // the opt-in. The two knobs below only TUNE it; they never gate it off.
        public bool autoUpgrade = true;

        // Global floor: a candidate below this quality is never acquired, whatever
        // the pawn currently holds ("never equip anything worse than Poor").
        public QualityCategory minQuality = QualityCategory.Poor;

        // Hit-point tiebreak granularity. When two candidates rank equal on the
        // primary axis (quality for guns, melee DPS for melee weapons), the one in a
        // higher hit-point BUCKET wins and equal buckets never swap — so a trivial
        // durability difference does not send a pawn across the map. Larger = calmer.
        public int hpBucket = 10;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref autoUpgrade, "autoUpgrade", true);
            Scribe_Values.Look(ref minQuality, "minQuality", QualityCategory.Poor);
            Scribe_Values.Look(ref hpBucket, "hpBucket", 10);
        }
    }

    public class LoadoutQualityMod : Mod
    {
        public static LQSettings Settings { get; private set; }

        public LoadoutQualityMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<LQSettings>();
        }

        public override string SettingsCategory() => "Loadout Quality";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.CheckboxLabeled("Auto weapon upgrade", ref Settings.autoUpgrade,
                "When idle, a pawn fetches the best available copy of each weapon its loadout "
                + "calls for — highest quality for guns, highest melee DPS for melee weapons (so "
                + "a better material wins), hit points breaking ties. The replaced weapon is "
                + "dropped in place, unforbidden, for your haulers.");

            listing.Gap();

            listing.Label($"Never acquire below: {Settings.minQuality}");
            Settings.minQuality = (QualityCategory)Mathf.RoundToInt(listing.Slider(
                (int)Settings.minQuality, (int)QualityCategory.Awful, (int)QualityCategory.Legendary));

            listing.Gap();

            listing.Label($"Hit-point tiebreak bucket: {Settings.hpBucket}",
                tooltip: "Two weapons that tie on quality (or melee DPS) only swap when their "
                         + "hit points land in different buckets of this size. Larger = fewer "
                         + "swaps over trivial durability gains.");
            Settings.hpBucket = Mathf.RoundToInt(listing.Slider(Settings.hpBucket, 1, 50));

            listing.End();
        }
    }

    /// <summary>Shared failure-doctrine guard (mirrors the suite's PatchGuard and
    /// BAO's BAOGuard): a patch's Prepare() proves its target still exists, and on a
    /// miss logs a named, player-readable consequence and returns false so that one
    /// class is skipped (inert) while the rest still apply.</summary>
    internal static class LQGuard
    {
        internal const string LogPrefix = "[Loadout Quality] ";

        internal static bool Require(Type type, string method, Type[] args, string consequence)
        {
            if (type != null && AccessTools.Method(type, method, args) != null)
            {
                return true;
            }
            Log.Error($"{LogPrefix}{type?.Name}.{method} not found — {consequence} "
                      + "Combat Extended probably moved it.");
            return false;
        }
    }

    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        public const string HarmonyId = "eebette.CELoadoutQuality";

        static Bootstrap()
        {
            // Per class, not PatchAll: an upstream member moving (CE renames a method
            // or a parameter — Harmony binds parameters by name, invisible to a
            // Prepare guard) costs THAT one patch with a named, player-readable error,
            // not the whole mod half-applied mid-assembly.
            var harmony = new Harmony(HarmonyId);
            int applied = 0;
            var failures = new List<string>();
            foreach (Type type in typeof(Bootstrap).Assembly.GetTypes())
            {
                try
                {
                    // Attribute probe INSIDE the try: decoding [HarmonyPatch] resolves
                    // its typeof() args, so upstream type-level drift there also costs
                    // one class, not the loop.
                    if (type.GetCustomAttributes(typeof(HarmonyPatch), inherit: false).Length == 0)
                    {
                        continue;
                    }
                    // A Prepare-false class returns no patched methods and is SKIPPED.
                    var patched = harmony.CreateClassProcessor(type).Patch();
                    if (patched != null && patched.Count > 0)
                    {
                        applied++;
                    }
                }
                catch (Exception e)
                {
                    failures.Add(type.Name);
                    Log.Error($"{LQGuard.LogPrefix}Patch class {type.Name} could not be applied — "
                              + $"that feature is inactive, the rest still work. {e}");
                }
            }
            if (failures.Count > 0)
            {
                Log.Warning($"{LQGuard.LogPrefix}Installed {applied} patch class(es); "
                            + $"{failures.Count} failed ({string.Join(", ", failures)}).");
            }
            else
            {
                Log.Message($"{LQGuard.LogPrefix}Installed {applied} patch class(es).");
            }

            // Clear the per-pawn sideline cache when CE clears its own (game load /
            // cache reset), so a stale entry cannot survive into another game. Wrapped
            // so a CE rename of this hook costs only the sideline-clear, not the mod.
            try
            {
                CacheClearComponent.AddClearCacheAction(GetUpdateLoadoutJob_Patch.ClearSidelines);
            }
            catch (Exception e)
            {
                Log.Warning($"{LQGuard.LogPrefix}Could not register the sideline cache-clear; a stale "
                            + $"back-off could persist across a reload until it expires. {e}");
            }
        }
    }
}
