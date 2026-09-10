using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PawnsOptimizeWeaponQuality
{
    public class PowqSettings : ModSettings
    {
        public bool autoUpgrade = true;

        // Global floor.
        public QualityCategory minQuality = QualityCategory.Poor;

        // Hit-point tiebreak granularity. Larger = calmer.
        public int hpBucket = 10;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref autoUpgrade, "autoUpgrade", true);
            Scribe_Values.Look(ref minQuality, "minQuality", QualityCategory.Poor);
            Scribe_Values.Look(ref hpBucket, "hpBucket", 10);
        }
    }

    public class PawnsOptimizeWeaponQualityMod : Mod
    {
        public static PowqSettings Settings { get; private set; }

        public PawnsOptimizeWeaponQualityMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<PowqSettings>();
        }

        public override string SettingsCategory() => "Pawns Optimize Weapon Quality";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.CheckboxLabeled("Auto weapon upgrade", ref Settings.autoUpgrade,
                "Idle pawns automatically equip the best available copy of each weapon they carry.");

            listing.Gap();

            listing.Label($"Never acquire below: {Settings.minQuality}");
            Settings.minQuality = (QualityCategory)Mathf.RoundToInt(listing.Slider(
                (int)Settings.minQuality, (int)QualityCategory.Awful, (int)QualityCategory.Legendary));

            listing.Gap();

            listing.Label($"Hit-point tiebreak bucket: {Settings.hpBucket}",
                tooltip: "Ties (same weapon, same quality) are broken based on hit point ranges. Larger = a bigger difference in durability required to trigger an upgrade.");
            Settings.hpBucket = Mathf.RoundToInt(listing.Slider(Settings.hpBucket, 1, 50));

            listing.End();
        }
    }

    /// <summary>Shared failure-doctrine guard.</summary>
    internal static class PowqGuard
    {
        internal const string LogPrefix = "[Pawns Optimize Weapon Quality] ";

        internal static bool Require(Type type, string method, Type[] args, string consequence)
        {
            if (type != null && AccessTools.Method(type, method, args) != null)
            {
                return true;
            }
            Log.Error($"{LogPrefix}{type?.Name}.{method} not found - {consequence} "
                      + "Combat Extended probably moved it.");
            return false;
        }
    }

    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        public const string HarmonyId = "eebette.PawnsOptimizeWeaponQuality";

        static Bootstrap()
        {
            var harmony = new Harmony(HarmonyId);
            int applied = 0;
            var failures = new List<string>();
            foreach (Type type in typeof(Bootstrap).Assembly.GetTypes())
            {
                try
                {
                    // Attribute probe INSIDE the try.
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
                    Log.Error($"{PowqGuard.LogPrefix}Patch class {type.Name} could not be applied - "
                              + $"that feature is inactive, the rest still work. {e}");
                }
            }
            if (failures.Count > 0)
            {
                Log.Warning($"{PowqGuard.LogPrefix}Installed {applied} patch class(es); "
                            + $"{failures.Count} failed ({string.Join(", ", failures)}).");
            }
            else
            {
                Log.Message($"{PowqGuard.LogPrefix}Installed {applied} patch class(es).");
            }

            // Clear the per-pawn sideline cache when CE clears its own.
            try
            {
                CombatExtendedBridge.RegisterCacheClear(GetUpdateLoadoutJob_Patch.ClearSidelines);
            }
            catch (Exception e)
            {
                Log.Warning($"{PowqGuard.LogPrefix}Could not register the sideline cache-clear; a stale "
                            + $"back-off could persist across a reload until it expires. {e}");
            }
        }
    }
}
