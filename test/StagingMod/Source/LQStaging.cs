using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using CombatExtended;
using PawnsOptimizeWeaponQuality;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace LQTestStaging
{
    /// <summary>
    /// Stages LQ saves (-quicktest -lqstage), CE-only profile (no Simple Sidearms):
    ///  LQ-1-ranged: "Ranged" holds a NORMAL bolt-action rifle, loadout {rifle};
    ///    an EXCELLENT rifle on the ground. Ranged upgrade path + off-control + the
    ///    dropped-unforbidden contract.
    ///  LQ-2-melee: "Melee" holds a STEEL NORMAL gladius, loadout {gladius}; a
    ///    PLASTEEL EXCELLENT gladius on the ground - a DIFFERENT material, which the
    ///    old same-stuff rule would have ignored. Melee ranks by MeleeWeapon_AverageDPS
    ///    (folds material), so the plasteel copy must win.
    ///  LQ-3-bucket: "Bucket" holds an EXCELLENT rifle damaged to a low HP bucket;
    ///    an EXCELLENT rifle at the SAME bucket (must NOT tempt a swap), then a
    ///    full-HP EXCELLENT rifle (higher bucket, must win). Pins the tiebreak both
    ///    ways.
    ///  LQ-4-floor: "Floor" holds an AWFUL rifle with the floor raised to Good; a
    ///    POOR rifle on the ground (better than Awful, below the floor - must be
    ///    refused), then an EXCELLENT rifle (above the floor, must win). Pins the
    ///    acquisition floor both ways.
    /// </summary>
    public class LQStagingComponent : GameComponent
    {
        private readonly List<Thing> staged = new List<Thing>();
        private readonly List<Loadout> stagedLoadouts = new List<Loadout>();
        private IntVec3 anchor = IntVec3.Invalid;

        public LQStagingComponent(Game game)
        {
        }

        public override void StartedNewGame()
        {
            if (!GenCommandLine.CommandLineArgPassed("lqstage"))
            {
                return;
            }
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    StageAll();
                }
                catch (Exception e)
                {
                    Log.Error("[LQStaging] Staging failed: " + e);
                }
            });
        }

        private void StageAll()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                Log.Error("[LQStaging] No map; launch with -quicktest -lqstage.");
                return;
            }
            anchor = ComputeAnchor(map);
            Log.Message($"[LQStaging] anchor {anchor}.");
            // Home area over the whole scene: without it, vanilla auto-forbids
            // anything pawns drop (outside-home-area convention), which would
            // false-fail the dropped-unforbidden contract that only holds inside a
            // colony's home area.
            foreach (IntVec3 c in GenRadial.RadialCellsAround(anchor, 25f, useCenter: true))
            {
                if (c.InBounds(map))
                {
                    map.areaManager.Home[c] = true;
                }
            }

            Stage1(map);
            SaveAndReset("LQ-1-ranged");
            Stage2(map);
            SaveAndReset("LQ-2-melee");
            Stage3(map);
            SaveAndReset("LQ-3-bucket");
            Stage4(map);
            SaveAndReset("LQ-4-floor");
            Stage5(map);
            SaveAndReset("LQ-5-meleeprime");

            Find.TickManager.Pause();
            Log.Message("[LQStaging] All LQ saves created.");
            Find.LetterStack.ReceiveLetter("LQ saves created",
                "LQ-1..5 written.", LetterDefOf.PositiveEvent);
        }

        private void Stage1(Map map)
        {
            Pawn pawn = SpawnColonist(map, "Ranged", new IntVec3(-6, 0, 0));
            ThingDef rifle = ThingDef.Named("Gun_BoltActionRifle");

            ThingWithComps carried = MakeWithQuality(rifle, null, QualityCategory.Normal);
            pawn.equipment.AddEquipment(carried);
            LoadMag(pawn, carried);
            SpawnWithQuality(map, rifle, null, QualityCategory.Excellent, anchor + new IntVec3(8, 0, 4));
            SpawnAmmoFor(map, rifle);

            GiveLoadout(pawn, "LQ ranged test", rifle);
        }

        private void Stage2(Map map)
        {
            Pawn pawn = SpawnColonist(map, "Melee", new IntVec3(-2, 0, 0));
            ThingDef rifle = ThingDef.Named("Gun_BoltActionRifle");
            ThingDef gladius = ThingDef.Named("MeleeWeapon_Gladius");

            // Rifle occupies the primary slot so the gladius stays an INVENTORY
            // sidearm - this exercises the inventory swap branch (lq1 covers the
            // equipped branch). No better rifle is staged, so the rifle slot is inert.
            ThingWithComps carriedRifle = MakeWithQuality(rifle, null, QualityCategory.Normal);
            pawn.equipment.AddEquipment(carriedRifle);
            LoadMag(pawn, carriedRifle);
            ThingWithComps carriedGladius = MakeWithQuality(gladius, ThingDefOf.Steel, QualityCategory.Normal);
            pawn.inventory.innerContainer.TryAdd(carriedGladius, true);
            SpawnAmmoFor(map, rifle);

            // Phase-0 bait: a SAME-MATERIAL, higher-QUALITY gladius at full HP. Every
            // gladius here is full HP, so the HP bucket is neutral and the swap can
            // only be driven by melee DPS. The material winner (plasteel) is staged by
            // the runner in phase 1. (The old lq2 relied on plasteel's higher HP
            // bucket, so it passed even when DPS was quality/material-blind.)
            SpawnWithQuality(map, gladius, ThingDefOf.Steel, QualityCategory.Excellent, anchor + new IntVec3(8, 0, -4));

            GiveLoadout(pawn, "LQ melee test", rifle, gladius);
        }

        private void Stage3(Map map)
        {
            Pawn pawn = SpawnColonist(map, "Bucket", new IntVec3(2, 0, 0));
            ThingDef rifle = ThingDef.Named("Gun_BoltActionRifle");

            // Carried Excellent rifle damaged to ~half HP (a low bucket). The
            // same-bucket bait sits at the same fraction; the full-HP winner is
            // staged in phase 1 by the runner (kept off the map until then).
            ThingWithComps carried = MakeWithQuality(rifle, null, QualityCategory.Excellent);
            carried.HitPoints = Mathf.Max(1, carried.MaxHitPoints / 2);
            pawn.equipment.AddEquipment(carried);
            LoadMag(pawn, carried);

            ThingWithComps sameBucket = MakeWithQuality(rifle, null, QualityCategory.Excellent);
            sameBucket.HitPoints = carried.HitPoints; // identical bucket - must not tempt a swap
            GenSpawn.Spawn(sameBucket, FindCell(map, anchor + new IntVec3(6, 0, 3)), map);
            staged.Add(sameBucket);
            SpawnAmmoFor(map, rifle);

            GiveLoadout(pawn, "LQ bucket test", rifle);
        }

        private void Stage4(Map map)
        {
            Pawn pawn = SpawnColonist(map, "Floor", new IntVec3(6, 0, 0));
            ThingDef rifle = ThingDef.Named("Gun_BoltActionRifle");

            ThingWithComps carried = MakeWithQuality(rifle, null, QualityCategory.Awful);
            pawn.equipment.AddEquipment(carried);
            LoadMag(pawn, carried);
            // Poor is better than the carried Awful but below the Good floor the
            // runner sets - must be refused. The Excellent winner is staged in
            // phase 1.
            SpawnWithQuality(map, rifle, null, QualityCategory.Poor, anchor + new IntVec3(6, 0, -3));
            SpawnAmmoFor(map, rifle);

            GiveLoadout(pawn, "LQ floor test", rifle);
        }

        private void Stage5(Map map)
        {
            Pawn pawn = SpawnColonist(map, "MeleePrime", new IntVec3(0, 0, 8));
            // Low melee skill so CE's damage-variation factor f = 0.75 + 0.025*skill is
            // BELOW 1 - the condition under which the old (equipped-skewed) ranking made
            // an identical ground copy out-measure the equipped weapon and ping-pong.
            pawn.skills.GetSkill(SkillDefOf.Melee).Level = 4;
            ThingDef gladius = ThingDef.Named("MeleeWeapon_Gladius");

            // Melee weapon as the EQUIPPED PRIMARY (not a sidearm) - the only case CE's
            // MeleeWeapon_AverageDPS skews. Steel Excellent equipped; an IDENTICAL steel
            // Excellent on the ground, full HP, must NOT tempt a swap. The material
            // winner (plasteel) is staged by the runner in phase 1.
            ThingWithComps carried = MakeWithQuality(gladius, ThingDefOf.Steel, QualityCategory.Excellent);
            pawn.equipment.AddEquipment(carried);
            SpawnWithQuality(map, gladius, ThingDefOf.Steel, QualityCategory.Excellent, anchor + new IntVec3(8, 0, 8));

            GiveLoadout(pawn, "LQ meleeprime test", gladius);
        }

        // ---- helpers -------------------------------------------------------

        private void GiveLoadout(Pawn pawn, string name, params ThingDef[] weapons)
        {
            var loadout = new Loadout(name);
            foreach (ThingDef w in weapons)
            {
                loadout.AddSlot(new LoadoutSlot(w, 1));
            }
            LoadoutManager.AddLoadout(loadout);
            stagedLoadouts.Add(loadout);
            pawn.SetLoadout(loadout);
        }

        private ThingWithComps MakeWithQuality(ThingDef def, ThingDef stuff, QualityCategory quality)
        {
            var thing = (ThingWithComps)ThingMaker.MakeThing(def, stuff ?? (def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null));
            thing.TryGetComp<CompQuality>()?.SetQuality(quality, ArtGenerationContext.Colony);
            return thing;
        }

        private void SpawnWithQuality(Map map, ThingDef def, ThingDef stuff, QualityCategory quality, IntVec3 near)
        {
            ThingWithComps thing = MakeWithQuality(def, stuff, quality);
            GenSpawn.Spawn(thing, FindCell(map, near), map);
            staged.Add(thing);
        }

        private void SpawnAmmoFor(Map map, ThingDef weapon)
        {
            AmmoDef ammo = weapon.GetCompProperties<CompProperties_AmmoUser>()?.ammoSet?.ammoTypes?.FirstOrDefault()?.ammo;
            if (ammo == null)
            {
                return;
            }
            Thing stack = ThingMaker.MakeThing(ammo);
            stack.stackCount = 120;
            GenSpawn.Spawn(stack, FindCell(map, anchor + new IntVec3(2, 0, -2)), map);
            staged.Add(stack);
        }

        private void LoadMag(Pawn pawn, ThingWithComps weapon)
        {
            CompAmmoUser user = weapon.TryGetComp<CompAmmoUser>();
            if (user != null && user.UseAmmo)
            {
                user.ResetAmmoCount();
            }
        }

        private void SaveAndReset(string name)
        {
            GameDataSaveLoader.SaveGame(name);
            foreach (Thing thing in staged)
            {
                if (thing is Pawn pawn)
                {
                    LoadoutManager._current?._assignedLoadouts?.Remove(pawn);
                    LoadoutManager._current?._assignedTrackers?.Remove(pawn);
                }
                if (thing != null && !thing.Destroyed)
                {
                    thing.Destroy(DestroyMode.Vanish);
                }
            }
            staged.Clear();
            foreach (Loadout loadout in stagedLoadouts)
            {
                LoadoutManager._current?._loadouts?.Remove(loadout);
            }
            stagedLoadouts.Clear();
        }

        private static IntVec3 ComputeAnchor(Map map)
        {
            bool Valid(IntVec3 c) => c.Standable(map) && !c.Fogged(map);
            if (CellFinder.TryFindRandomCellNear(map.Center, map, 30, Valid, out IntVec3 cell))
            {
                return cell;
            }
            CellFinderLoose.TryGetRandomCellWith(Valid, map, 1000, out cell);
            return cell.IsValid ? cell : map.Center;
        }

        private IntVec3 FindCell(Map map, IntVec3 near)
        {
            IntVec3 root = near.ClampInsideMap(map);
            if (CellFinder.TryFindRandomCellNear(root, map, 15, c => c.Standable(map) && !c.Fogged(map), out IntVec3 cell))
            {
                return cell;
            }
            return anchor;
        }

        private Pawn SpawnColonist(Map map, string nick, IntVec3 offset)
        {
            var request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                          PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true,
                          canGeneratePawnRelations: false, colonistRelationChanceFactor: 0f);
            Pawn pawn = PawnGenerator.GeneratePawn(request);
            pawn.Name = new NameTriple("Test", nick, "LQ");
            pawn.equipment?.DestroyAllEquipment();
            pawn.inventory?.DestroyAll();
            GenSpawn.Spawn(pawn, FindCell(map, anchor + offset), map);
            staged.Add(pawn);
            return pawn;
        }
    }

    [StaticConstructorOnStartup]
    public static class LQTestBoot
    {
        static LQTestBoot()
        {
            Log.Message("[LQStaging] assembly loaded.");
            if (!GenCommandLine.TryGetCommandLineArg("ceassert", out string scenario)
                || scenario.NullOrEmpty() || !scenario.StartsWith("lq"))
            {
                return;
            }
            // Kill the default-ON upgrade BEFORE any save can load and tick - the
            // LoadedGame-callback reset loses a race against the think tree on a save
            // that loads unpaused (each scenario re-enables explicitly).
            PawnsOptimizeWeaponQualityMod.Settings.autoUpgrade = false;
            if (GenCommandLine.TryGetCommandLineArg("celoadsave", out string save) && !save.NullOrEmpty())
            {
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    Log.Message($"[LQTest] Auto-loading save '{save}'.");
                    GameDataSaveLoader.LoadGame(save);
                });
            }
        }
    }

    public class LQTestRunnerComponent : GameComponent
    {
        private bool active;
        private bool done;
        private int startTick;
        private int phase;
        private string scenario;
        private Pawn subject;
        private readonly List<string> results = new List<string>();
        private bool failed;

        // bucket-scenario bookkeeping
        private int carriedBucketId;
        private int carriedBucketValue;
        private int primeId; // meleeprime-scenario: the initially-equipped instance

        public LQTestRunnerComponent(Game game)
        {
        }

        public override void LoadedGame()
        {
            if (!GenCommandLine.TryGetCommandLineArg("ceassert", out scenario)
                || scenario.NullOrEmpty() || !scenario.StartsWith("lq"))
            {
                return;
            }
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                string nick = NickFor(scenario);
                subject = Find.CurrentMap?.mapPawns.FreeColonistsSpawned
                    .FirstOrDefault(p => p.Name is NameTriple nt && nt.Nick == nick);
                if (subject == null)
                {
                    Check("setup", false, $"subject pawn '{nick}' missing");
                    Finish();
                    return;
                }
                PawnsOptimizeWeaponQualityMod.Settings.autoUpgrade = false; // each scenario opts in explicitly
                PawnsOptimizeWeaponQualityMod.Settings.minQuality = QualityCategory.Poor;
                PawnsOptimizeWeaponQualityMod.Settings.hpBucket = 10;
                active = true;
                startTick = Find.TickManager.TicksGame;
                Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
                Log.Message($"[LQTest] {scenario} started.");
            });
        }

        private static string NickFor(string s)
        {
            switch (s)
            {
                case "lq1": return "Ranged";
                case "lq2": return "Melee";
                case "lq3": return "Bucket";
                case "lq4": return "Floor";
                case "lq5": return "MeleePrime";
                default: return "?";
            }
        }

        private void Check(string name, bool pass, string detail)
        {
            results.Add($"{{\"name\": \"{name}\", \"passed\": {(pass ? "true" : "false")}, \"detail\": \"{detail.Replace("\"", "'")}\"}}");
            if (!pass)
            {
                failed = true;
            }
            Log.Message($"[LQTest] {name}: {(pass ? "PASS" : "FAIL")} - {detail}");
        }

        public override void GameComponentTick()
        {
            if (!active || done)
            {
                return;
            }
            if (Find.TickManager.Paused || Find.TickManager.CurTimeSpeed != TimeSpeed.Superfast)
            {
                Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
            }
            int tick = Find.TickManager.TicksGame;
            if (tick % 30 != 0)
            {
                return;
            }
            switch (scenario)
            {
                case "lq1": TickRanged(tick); break;
                case "lq2": TickMelee(tick); break;
                case "lq3": TickBucket(tick); break;
                case "lq4": TickFloor(tick); break;
                case "lq5": TickMeleePrime(tick); break;
            }
        }

        private ThingWithComps Primary => subject.equipment?.Primary;

        // LQ-1: phase 0 (toggle OFF) - normal rifle retained 1800 ticks; phase 1
        // (ON) - swaps to the excellent rifle, the normal one dropped unforbidden.
        private void TickRanged(int tick)
        {
            ThingDef rifle = ThingDef.Named("Gun_BoltActionRifle");
            if (phase == 0)
            {
                Primary.TryGetQuality(out QualityCategory q);
                if (q != QualityCategory.Normal)
                {
                    Check("off-no-upgrade", false, $"swapped with toggle OFF: quality={q}");
                    Finish();
                    return;
                }
                if (tick - startTick > 1800)
                {
                    Check("off-no-upgrade", true, "normal rifle retained for 1800 ticks");
                    PawnsOptimizeWeaponQualityMod.Settings.autoUpgrade = true;
                    phase = 1;
                    startTick = tick;
                }
                return;
            }
            Primary.TryGetQuality(out QualityCategory pq);
            if (Primary?.def == rifle && pq == QualityCategory.Excellent)
            {
                Check("ranged-upgrade-swaps", true, "excellent rifle equipped");
                Thing droppedNormal = NearbyDropped(rifle, t =>
                    t.TryGetQuality(out QualityCategory dq) && dq == QualityCategory.Normal);
                Check("old-dropped-unforbidden",
                    droppedNormal != null && !droppedNormal.IsForbidden(Faction.OfPlayer),
                    $"dropped={(droppedNormal != null)} forbidden={droppedNormal?.IsForbidden(Faction.OfPlayer)}");
                Finish();
                return;
            }
            Timeout("ranged-upgrade-swaps", tick, $"primary={Primary?.def?.defName}:{pq} job={subject.CurJobDef?.defName}");
        }

        // LQ-2: melee ranks by MeleeWeapon_AverageDPS (folds material AND quality),
        // with HP held equal so the HP bucket cannot be what drives the swap. Phase 0
        // (same material): a steel Excellent gladius must beat the carried steel Normal
        // one on QUALITY. Phase 1 (same quality): a plasteel Excellent gladius must
        // beat the steel Excellent one on MATERIAL. All on the inventory-sidearm branch.
        private ThingWithComps InvGladius() => subject.inventory.innerContainer.OfType<ThingWithComps>()
            .FirstOrDefault(t => t.def.defName == "MeleeWeapon_Gladius");

        private static QualityCategory QualityOf(Thing t)
        {
            t.TryGetQuality(out QualityCategory q);
            return q;
        }

        private void TickMelee(int tick)
        {
            PawnsOptimizeWeaponQualityMod.Settings.autoUpgrade = true; // no off-control here (lq1 covers it)
            if (phase == 0)
            {
                ThingWithComps g = InvGladius();
                if (g != null && g.Stuff == ThingDefOf.Steel && QualityOf(g) == QualityCategory.Excellent)
                {
                    Check("melee-quality-upgrade-same-material", true,
                        "steel Excellent gladius acquired over steel Normal (DPS folds quality, HP neutral)");
                    // Stage the material winner: a plasteel Excellent gladius, full HP.
                    var plasteel = (ThingWithComps)ThingMaker.MakeThing(
                        ThingDef.Named("MeleeWeapon_Gladius"), ThingDefOf.Plasteel);
                    plasteel.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Excellent, ArtGenerationContext.Colony);
                    GenSpawn.Spawn(plasteel, CellFinder.RandomClosewalkCellNear(subject.Position, subject.Map, 4), subject.Map);
                    phase = 1;
                    startTick = tick;
                    return;
                }
                Timeout("melee-quality-upgrade-same-material", tick,
                    $"invGladius={g?.Stuff?.defName}:{(g != null ? QualityOf(g).ToString() : "none")} job={subject.CurJobDef?.defName}");
                return;
            }
            ThingWithComps gg = InvGladius();
            if (gg != null && gg.Stuff == ThingDefOf.Plasteel)
            {
                Check("melee-material-upgrade", true,
                    "plasteel Excellent gladius acquired over steel Excellent (DPS folds material at equal quality)");
                Thing droppedSteel = NearbyDropped(ThingDef.Named("MeleeWeapon_Gladius"),
                    t => t.Stuff == ThingDefOf.Steel && QualityOf(t) == QualityCategory.Excellent);
                Check("old-dropped-unforbidden",
                    droppedSteel != null && !droppedSteel.IsForbidden(Faction.OfPlayer),
                    $"dropped={(droppedSteel != null)} forbidden={droppedSteel?.IsForbidden(Faction.OfPlayer)}");
                Finish();
                return;
            }
            Timeout("melee-material-upgrade", tick,
                $"invGladius={gg?.Stuff?.defName ?? "none"} job={subject.CurJobDef?.defName}");
        }

        // LQ-3: HP-bucket tiebreak. Phase 0 (ON) - an excellent rifle at the SAME
        // bucket as carried must NOT tempt a swap for 1800 ticks. Phase 1 - a
        // full-HP excellent rifle (higher bucket) must win.
        private void TickBucket(int tick)
        {
            ThingDef rifle = ThingDef.Named("Gun_BoltActionRifle");
            int bucketSize = PawnsOptimizeWeaponQualityMod.Settings.hpBucket;
            if (phase == 0)
            {
                if (carriedBucketId == 0)
                {
                    PawnsOptimizeWeaponQualityMod.Settings.autoUpgrade = true;
                    carriedBucketId = Primary.thingIDNumber;
                    carriedBucketValue = Primary.HitPoints / bucketSize;
                }
                if (Primary == null || Primary.thingIDNumber != carriedBucketId)
                {
                    Check("equal-bucket-no-thrash", false,
                        $"swapped to a same-bucket copy: primaryId={Primary?.thingIDNumber}");
                    Finish();
                    return;
                }
                if (tick - startTick > 1800)
                {
                    Check("equal-bucket-no-thrash", true, "same-bucket copy ignored for 1800 ticks");
                    // Now stage the higher-bucket winner: a full-HP excellent rifle.
                    ThingWithComps winner = MakeExcellent(rifle);
                    winner.HitPoints = winner.MaxHitPoints;
                    GenSpawn.Spawn(winner, CellFinder.RandomClosewalkCellNear(subject.Position, subject.Map, 4), subject.Map);
                    phase = 1;
                    startTick = tick;
                }
                return;
            }
            if (Primary != null && Primary.thingIDNumber != carriedBucketId
                && Primary.HitPoints / bucketSize > carriedBucketValue)
            {
                Check("higher-bucket-swaps", true,
                    $"swapped to higher-bucket copy (hp={Primary.HitPoints}, bucket={Primary.HitPoints / bucketSize} > {carriedBucketValue})");
                Finish();
                return;
            }
            Timeout("higher-bucket-swaps", tick,
                $"primaryHp={Primary?.HitPoints} bucket={(Primary != null ? Primary.HitPoints / bucketSize : -1)} job={subject.CurJobDef?.defName}");
        }

        // LQ-4: acquisition floor. Phase 0 (ON, floor Good) - a Poor rifle (better
        // than the carried Awful, below the floor) must be refused. Phase 1 - an
        // Excellent rifle (above the floor) must win.
        private void TickFloor(int tick)
        {
            ThingDef rifle = ThingDef.Named("Gun_BoltActionRifle");
            if (phase == 0)
            {
                if (tick == startTick + 30 || !PawnsOptimizeWeaponQualityMod.Settings.autoUpgrade)
                {
                    PawnsOptimizeWeaponQualityMod.Settings.autoUpgrade = true;
                    PawnsOptimizeWeaponQualityMod.Settings.minQuality = QualityCategory.Good;
                }
                Primary.TryGetQuality(out QualityCategory q);
                if (q != QualityCategory.Awful)
                {
                    Check("floor-refuses-below", false, $"acquired below-floor weapon: quality={q}");
                    Finish();
                    return;
                }
                if (tick - startTick > 1800)
                {
                    Check("floor-refuses-below", true, "Poor rifle refused under the Good floor; Awful retained");
                    ThingWithComps winner = MakeExcellent(rifle);
                    GenSpawn.Spawn(winner, CellFinder.RandomClosewalkCellNear(subject.Position, subject.Map, 4), subject.Map);
                    phase = 1;
                    startTick = tick;
                }
                return;
            }
            Primary.TryGetQuality(out QualityCategory pq);
            if (Primary?.def == rifle && pq == QualityCategory.Excellent)
            {
                Check("above-floor-swaps", true, "excellent rifle acquired once available");
                Finish();
                return;
            }
            Timeout("above-floor-swaps", tick, $"primary={Primary?.def?.defName}:{pq} job={subject.CurJobDef?.defName}");
        }

        // LQ-5: melee weapon as the EQUIPPED PRIMARY, melee skill 4 (f<1). Phase 0: an
        // IDENTICAL steel Excellent ground copy must NOT be swapped to (the old
        // equipped-skew ranking would swap and ping-pong). Phase 1: a plasteel Excellent
        // copy (material upgrade) must win - proving equipped-primary melee still
        // upgrades correctly.
        private void TickMeleePrime(int tick)
        {
            PawnsOptimizeWeaponQualityMod.Settings.autoUpgrade = true;
            ThingDef gladius = ThingDef.Named("MeleeWeapon_Gladius");
            if (phase == 0)
            {
                if (primeId == 0 && subject.equipment?.Primary != null)
                {
                    primeId = subject.equipment.Primary.thingIDNumber;
                }
                ThingWithComps p = subject.equipment?.Primary;
                if (p == null || p.thingIDNumber != primeId)
                {
                    Check("meleeprime-no-pingpong", false,
                        $"swapped off the equipped weapon (id {p?.thingIDNumber} != {primeId}) - skew ping-pong");
                    Finish();
                    return;
                }
                if (tick - startTick > 1800)
                {
                    Check("meleeprime-no-pingpong", true,
                        "steel Excellent primary held against an identical ground copy (no skew ping-pong)");
                    var plasteel = (ThingWithComps)ThingMaker.MakeThing(gladius, ThingDefOf.Plasteel);
                    plasteel.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Excellent, ArtGenerationContext.Colony);
                    GenSpawn.Spawn(plasteel, CellFinder.RandomClosewalkCellNear(subject.Position, subject.Map, 4), subject.Map);
                    phase = 1;
                    startTick = tick;
                }
                return;
            }
            ThingWithComps pp = subject.equipment?.Primary;
            if (pp != null && pp.def == gladius && pp.Stuff == ThingDefOf.Plasteel)
            {
                Check("meleeprime-material-upgrade", true,
                    "plasteel Excellent equipped as primary (material upgrade, no skew)");
                Finish();
                return;
            }
            Timeout("meleeprime-material-upgrade", tick,
                $"primary={pp?.Stuff?.defName} job={subject.CurJobDef?.defName}");
        }

        private ThingWithComps MakeExcellent(ThingDef def)
        {
            var thing = (ThingWithComps)ThingMaker.MakeThing(def, def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null);
            thing.TryGetComp<CompQuality>()?.SetQuality(QualityCategory.Excellent, ArtGenerationContext.Colony);
            return thing;
        }

        // Pawn-adjacent instance ONLY - quicktest maps scatter their own random
        // weapons, and a map-wide search once sampled a pre-existing forbidden weapon
        // from across the map (id mismatch caught by fingerprinting).
        private Thing NearbyDropped(ThingDef def, Predicate<Thing> match)
        {
            return subject.Map.listerThings.ThingsOfDef(def)
                .Where(t => t.Spawned && t.Position.DistanceTo(subject.Position) < 8f)
                .FirstOrDefault(t => match(t));
        }

        private void Timeout(string name, int tick, string detail)
        {
            if (tick - startTick > 30000)
            {
                Check(name, false, detail);
                Finish();
            }
        }

        private void Finish()
        {
            done = true;
            var sb = new StringBuilder();
            sb.Append($"{{\n  \"scenario\": \"{scenario}\",\n");
            sb.Append($"  \"passed\": {(!failed ? "true" : "false")},\n");
            sb.Append("  \"checks\": [\n    ");
            sb.Append(string.Join(",\n    ", results));
            sb.Append("\n  ]\n}\n");
            File.WriteAllText(Path.Combine(GenFilePaths.SaveDataFolderPath, $"test-results-{scenario}.json"), sb.ToString());
            Log.Message("[LQTest] Results written; shutting down.");
            Root.Shutdown();
        }
    }
}
