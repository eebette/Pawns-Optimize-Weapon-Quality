using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using PawnsOptimizeWeaponQuality;
using RimWorld;
using Verse;

namespace LQTestStagingVanilla
{
    /// <summary>
    /// Single-phase vanilla-profile test (Core + Harmony + PawnsOptimizeWeaponQuality, NO Combat Extended).
    /// Proves the no-CE path end to end: with CE absent the mod's vanilla trigger
    /// (JobGiver_OptimizeApparel postfix) fires for idle colonists, the held-weapon scan finds
    /// better map copies, and the swap job runs -
    ///  Subject One:
    ///   - ranged: an equipped NORMAL bolt-action rifle upgrades to an EXCELLENT copy (quality key),
    ///   - melee: a NORMAL steel gladius carried in inventory upgrades to an EXCELLENT steel copy
    ///     (the abstract melee key ties on material, quality breaks it),
    ///   - both replaced NORMAL copies land on the ground UNFORBIDDEN.
    ///  Subject Two:
    ///   - melee EQUIPPED PRIMARY: a NORMAL steel gladius equipped as the primary upgrades to an
    ///     EXCELLENT steel copy. This is the round-3 wielder-skew path; the wielder-free abstract
    ///     key must still upgrade on quality and never ping-pong on a holder term.
    /// Inert without -lqvtest.
    /// </summary>
    public class LQVanillaTestComponent : GameComponent
    {
        private const string RifleDefName = "Gun_BoltActionRifle";
        private const string GladiusDefName = "MeleeWeapon_Gladius";
        private const string Scenario = "lqv";

        private bool active;
        private bool done;
        private int startTick;

        private Pawn subject;                        // equipped rifle + inventory gladius
        private ThingWithComps startRifle;           // NORMAL equipped rifle (identity guard)
        private ThingWithComps startGladius;         // NORMAL inventory gladius (identity guard)

        private Pawn subject2;                        // equipped melee PRIMARY (wielder-skew path)
        private ThingWithComps startEquippedGladius;  // NORMAL steel gladius equipped by subject2

        private bool verifyMode;

        private readonly List<string> results = new List<string>();
        private bool failed;

        public LQVanillaTestComponent(Game game)
        {
        }

        public override void StartedNewGame()
        {
            // Manual dev-record staging: arrange a hand-recordable POWQ scene and save it.
            if (GenCommandLine.CommandLineArgPassed("powqstage"))
            {
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    try
                    {
                        StagePOWQ();
                    }
                    catch (Exception e)
                    {
                        Log.Error("[POWQstage] arrange/save threw: " + e);
                        Root.Shutdown();
                    }
                });
                return;
            }
            // Verify the strip-free promo flow end to end.
            if (GenCommandLine.CommandLineArgPassed("powqverify"))
            {
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    try
                    {
                        VerifyFlow();
                    }
                    catch (Exception e)
                    {
                        Check("setup", false, "verify threw: " + e.Message);
                        Finish();
                    }
                });
                return;
            }
            if (!GenCommandLine.CommandLineArgPassed("lqvtest"))
            {
                return;
            }
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    Arrange();
                }
                catch (Exception e)
                {
                    Check("setup", false, "arrange threw: " + e.Message);
                    Finish();
                }
            });
        }

        private const string PowqGunDef = "Gun_ChargeRifle";

        /// <summary>
        /// Build the vanilla dev scene on the quicktest map: a high-Shooting colonist with a NORMAL
        /// charge rifle facing a plainly-dressed, low-Shooting pirate holding an EXCELLENT one (same
        /// def, so POWQ optimizes Normal -> Excellent). Clears the quicktest defaults so only these
        /// two remain. Returns false if there is no map.
        /// </summary>
        private bool BuildDevScene(Map map, out Pawn colonist, out Pawn raider)
        {
            colonist = null;
            raider = null;
            if (map == null)
            {
                return false;
            }
            IntVec3 colonistAt = map.Center;
            if (!colonistAt.Standable(map))
            {
                CellFinder.TryFindRandomCellNear(map.Center, map, 12,
                    c => c.Standable(map) && !c.Fogged(map), out colonistAt);
            }
            IntVec3 raiderAt = (colonistAt + new IntVec3(5, 0, 0)).ClampInsideMap(map);
            if (!raiderAt.Standable(map))
            {
                CellFinder.TryFindRandomCellNear(raiderAt, map, 8,
                    c => c.Standable(map) && !c.Fogged(map), out raiderAt);
            }
            foreach (IntVec3 site in new[] { colonistAt, raiderAt })
            {
                foreach (IntVec3 c in GenRadial.RadialCellsAround(site, 20f, useCenter: true))
                {
                    if (c.InBounds(map))
                    {
                        map.areaManager.Home[c] = true;
                    }
                }
            }

            // Colonist FIRST so the map is never colonist-empty when the quicktest defaults are cleared.
            colonist = SpawnFactionPawn(map, colonistAt, PawnKindDefOf.Colonist, Faction.OfPlayer, "Ace", "Deadeye");
            SetShooting(colonist, 20, Passion.Major);
            colonist.equipment.AddEquipment(MakeWeapon(PowqGunDef, null, QualityCategory.Normal));

            foreach (Pawn p in map.mapPawns.AllPawnsSpawned.ToList())
            {
                if (p != colonist)
                {
                    p.Destroy();
                }
            }
            foreach (Thing w in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon).ToList())
            {
                if (w.Spawned && !w.Destroyed)
                {
                    w.Destroy();
                }
            }

            raider = SpawnFactionPawn(map, raiderAt, PawnKindDefOf.Pirate, Faction.OfPirates, "Bandit", "Butterfingers");
            SetShooting(raider, 0, Passion.None);
            // Strip the pawnkind's random armor (the "only a flak vest" look); dress it plainly so it
            // reads normal and downs fast.
            raider.apparel?.DestroyAll();
            Wear(raider, "Apparel_Pants", ThingDefOf.Cloth);
            Wear(raider, "Apparel_CollarShirt", ThingDefOf.Cloth);
            raider.equipment.AddEquipment(MakeWeapon(PowqGunDef, null, QualityCategory.Excellent));

            PawnsOptimizeWeaponQualityMod.Settings.autoUpgrade = true;
            PawnsOptimizeWeaponQualityMod.Settings.minQuality = QualityCategory.Poor;
            return true;
        }

        private void StagePOWQ()
        {
            if (!BuildDevScene(Find.CurrentMap, out _, out _))
            {
                Log.Error("[POWQstage] no map; launch with -quicktest -powqstage");
                Root.Shutdown();
                return;
            }
            Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            GameDataSaveLoader.SaveGame("POWQ-DevRecord");
            Log.Message("[POWQstage] saved POWQ-DevRecord (Ace Shooting 20 / normal charge rifle vs "
                        + "Bandit Shooting 0 / excellent charge rifle). Shutting down.");
            Root.Shutdown();
        }

        /// <summary>
        /// Prove the promotional flow with NO strip/un-forbid clicks: kill the raider (drops a
        /// FORBIDDEN excellent charge rifle, exactly as a player kill would), leave the colonist idle
        /// and undrafted, and confirm PowqDevUnforbidComponent clears the flag and POWQ walks the
        /// colonist over to swap up in quality on its own.
        /// </summary>
        private void VerifyFlow()
        {
            if (!BuildDevScene(Find.CurrentMap, out subject, out Pawn raider))
            {
                Check("setup", false, "no map; launch with -quicktest -powqverify");
                Finish();
                return;
            }
            startRifle = subject.equipment.Primary;   // NORMAL charge rifle (identity guard)
            raider.Kill(null);                         // death drop -> forbidden gun
            verifyMode = true;
            active = true;
            startTick = Find.TickManager.TicksGame;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
            Log.Message("[POWQverify] raider killed; polling for auto-unforbid + POWQ swap (no strip/click).");
        }

        private Pawn SpawnFactionPawn(Map map, IntVec3 at, PawnKindDef kind, Faction faction,
                                      string first, string nick)
        {
            var request = new PawnGenerationRequest(kind, faction,
                PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true,
                canGeneratePawnRelations: false, colonistRelationChanceFactor: 0f);
            Pawn pawn = PawnGenerator.GeneratePawn(request);
            pawn.Name = new NameTriple(first, nick, "Test");
            pawn.equipment?.DestroyAllEquipment();
            pawn.inventory?.DestroyAll();
            GenSpawn.Spawn(pawn, at, map);
            return pawn;
        }

        private static void Wear(Pawn pawn, string defName, ThingDef stuff)
        {
            ThingDef def = ThingDef.Named(defName);
            var apparel = (Apparel)ThingMaker.MakeThing(def, def.MadeFromStuff ? stuff : null);
            pawn.apparel?.Wear(apparel, dropReplacedApparel: false);
        }

        private static void SetShooting(Pawn pawn, int level, Passion passion)
        {
            SkillRecord sk = pawn.skills?.GetSkill(SkillDefOf.Shooting);
            if (sk != null)
            {
                sk.Level = level;
                sk.passion = passion;
                sk.xpSinceLastLevel = 0f;
            }
        }

        private void Arrange()
        {
            Map map = Find.CurrentMap;
            if (map == null)
            {
                Check("setup", false, "no map; launch with -quicktest -lqvtest");
                Finish();
                return;
            }

            if (!CellFinder.TryFindRandomCellNear(map.Center, map, 15,
                    c => c.Standable(map) && !c.Fogged(map), out IntVec3 anchor))
            {
                anchor = map.Center;
            }
            // A second staging site, well clear of the first so the two pawns never contend
            // for each other's ground copies.
            if (!CellFinder.TryFindRandomCellNear(map.Center, map, 40,
                    c => c.Standable(map) && !c.Fogged(map) && c.DistanceTo(anchor) > 22f, out IntVec3 anchor2))
            {
                anchor2 = (anchor + new IntVec3(24, 0, 0)).ClampInsideMap(map);
            }
            // Home area over both sites so vanilla doesn't auto-forbid dropped weapons
            // (the outside-home-area convention would false-fail the unforbidden contract).
            foreach (IntVec3 site in new[] { anchor, anchor2 })
            {
                foreach (IntVec3 c in GenRadial.RadialCellsAround(site, 18f, useCenter: true))
                {
                    if (c.InBounds(map))
                    {
                        map.areaManager.Home[c] = true;
                    }
                }
            }

            // Subject One: equipped ranged primary + inventory melee sidearm.
            subject = SpawnColonist(map, anchor, "One");
            startRifle = MakeWeapon(RifleDefName, null, QualityCategory.Normal);
            subject.equipment.AddEquipment(startRifle);
            startGladius = MakeWeapon(GladiusDefName, ThingDefOf.Steel, QualityCategory.Normal);
            subject.inventory.innerContainer.TryAdd(startGladius, canMergeWithExistingStacks: true);
            DropNear(map, anchor, MakeWeapon(RifleDefName, null, QualityCategory.Excellent));
            DropNear(map, anchor, MakeWeapon(GladiusDefName, ThingDefOf.Steel, QualityCategory.Excellent));

            // Subject Two: equipped melee PRIMARY - the round-3 wielder-skew path.
            subject2 = SpawnColonist(map, anchor2, "Two");
            startEquippedGladius = MakeWeapon(GladiusDefName, ThingDefOf.Steel, QualityCategory.Normal);
            subject2.equipment.AddEquipment(startEquippedGladius);
            DropNear(map, anchor2, MakeWeapon(GladiusDefName, ThingDefOf.Steel, QualityCategory.Excellent));

            PawnsOptimizeWeaponQualityMod.Settings.autoUpgrade = true;
            PawnsOptimizeWeaponQualityMod.Settings.minQuality = QualityCategory.Poor;
            PawnsOptimizeWeaponQualityMod.Settings.hpBucket = 10;

            active = true;
            startTick = Find.TickManager.TicksGame;
            Find.TickManager.CurTimeSpeed = TimeSpeed.Superfast;
            Log.Message("[LQVanilla] arranged; polling for swaps.");
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
            if (verifyMode)
            {
                int vt = Find.TickManager.TicksGame;
                if (vt % 30 != 0)
                {
                    return;
                }
                ThingWithComps vEq = subject.equipment?.Primary;
                ThingDef gun = ThingDef.Named(PowqGunDef);
                bool swapped = vEq != null && vEq != startRifle && vEq.def == gun
                               && vEq.TryGetQuality(out QualityCategory vq) && vq > QualityCategory.Normal;
                if (swapped)
                {
                    Check("promo-flow-autograb", true,
                        $"colonist equipped a {Q(vEq)} charge rifle with only draft/undraft - no strip, no un-forbid click");
                    Finish();
                    return;
                }
                if (vt - startTick > 30000)
                {
                    Check("promo-flow-autograb", false,
                        $"no swap after 30000 ticks; equipped={(vEq == null ? "none" : Q(vEq))}");
                    Finish();
                }
                return;
            }
            int tick = Find.TickManager.TicksGame;
            if (tick % 30 != 0)
            {
                return;
            }

            ThingDef rifleDef = ThingDef.Named(RifleDefName);
            ThingDef gladiusDef = ThingDef.Named(GladiusDefName);

            ThingWithComps equipped = subject.equipment?.Primary;
            bool rifleDone = equipped != null && equipped != startRifle && equipped.def == rifleDef
                             && equipped.TryGetQuality(out QualityCategory rq) && rq > QualityCategory.Normal;

            ThingWithComps invGladius = subject.inventory?.innerContainer?.OfType<ThingWithComps>()
                .FirstOrDefault(t => t.def == gladiusDef);
            bool invGladiusDone = invGladius != null && invGladius != startGladius
                                  && invGladius.TryGetQuality(out QualityCategory gq) && gq > QualityCategory.Normal;

            ThingWithComps eqGladius = subject2.equipment?.Primary;
            bool eqGladiusDone = eqGladius != null && eqGladius != startEquippedGladius && eqGladius.def == gladiusDef
                                 && eqGladius.TryGetQuality(out QualityCategory eq) && eq > QualityCategory.Normal;

            if (rifleDone && invGladiusDone && eqGladiusDone)
            {
                Check("ranged-equipped-swap", true, $"subject One equipped a {Q(equipped)} rifle (was Normal)");
                Check("melee-inventory-swap", true, $"subject One inventory holds a {Q(invGladius)} gladius (was Normal)");
                Check("melee-equipped-swap", true, $"subject Two equipped a {Q(eqGladius)} gladius (was Normal, wielder-free key)");
                bool droppedOk = startRifle.Spawned && !startRifle.IsForbidden(subject)
                                 && startGladius.Spawned && !startGladius.IsForbidden(subject)
                                 && startEquippedGladius.Spawned && !startEquippedGladius.IsForbidden(subject2);
                Check("dropped-unforbidden", droppedOk,
                    $"old rifle spawned={startRifle.Spawned}; inv gladius spawned={startGladius.Spawned}; "
                    + $"eq gladius spawned={startEquippedGladius.Spawned} (all should be unforbidden on the ground)");
                Finish();
                return;
            }

            if (tick - startTick > 30000)
            {
                Check("ranged-equipped-swap", rifleDone,
                    rifleDone ? "ok" : $"no swap after 30000 ticks; equipped={(equipped == null ? "none" : Q(equipped))}");
                Check("melee-inventory-swap", invGladiusDone,
                    invGladiusDone ? "ok" : $"no swap after 30000 ticks; inventory gladius={(invGladius == null ? "none" : Q(invGladius))}");
                Check("melee-equipped-swap", eqGladiusDone,
                    eqGladiusDone ? "ok" : $"no swap after 30000 ticks; subject2 equipped={(eqGladius == null ? "none" : Q(eqGladius))}");
                Finish();
            }
        }

        private static string Q(Thing t) => t.TryGetQuality(out QualityCategory q) ? q.ToString() : "?";

        private Pawn SpawnColonist(Map map, IntVec3 at, string nick)
        {
            var request = new PawnGenerationRequest(PawnKindDefOf.Colonist, Faction.OfPlayer,
                PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true,
                canGeneratePawnRelations: false, colonistRelationChanceFactor: 0f);
            Pawn pawn = PawnGenerator.GeneratePawn(request);
            pawn.Name = new NameTriple("Test", nick, "LQ");
            pawn.equipment?.DestroyAllEquipment();
            pawn.inventory?.DestroyAll();
            GenSpawn.Spawn(pawn, at, map);
            return pawn;
        }

        private static ThingWithComps MakeWeapon(string defName, ThingDef stuff, QualityCategory quality)
        {
            ThingDef def = ThingDef.Named(defName);
            var weapon = (ThingWithComps)ThingMaker.MakeThing(def,
                def.MadeFromStuff ? (stuff ?? GenStuff.DefaultStuffFor(def)) : null);
            weapon.TryGetComp<CompQuality>()?.SetQuality(quality, ArtGenerationContext.Colony);
            return weapon;
        }

        private void DropNear(Map map, IntVec3 near, ThingWithComps weapon)
        {
            GenPlace.TryPlaceThing(weapon, near, map, ThingPlaceMode.Near);
            weapon.SetForbidden(false, warnOnFail: false);
        }

        private void Check(string name, bool pass, string detail)
        {
            results.Add($"{{\"name\": \"{name}\", \"passed\": {(pass ? "true" : "false")}, \"detail\": \"{detail.Replace("\"", "'")}\"}}");
            if (!pass)
            {
                failed = true;
            }
            Log.Message($"[LQVanilla] {name}: {(pass ? "PASS" : "FAIL")} - {detail}");
        }

        private void Finish()
        {
            done = true;
            var sb = new StringBuilder();
            sb.Append($"{{\n  \"scenario\": \"{Scenario}\",\n");
            sb.Append($"  \"passed\": {(!failed ? "true" : "false")},\n");
            sb.Append("  \"checks\": [\n    ");
            sb.Append(string.Join(",\n    ", results));
            sb.Append("\n  ]\n}\n");
            File.WriteAllText(Path.Combine(GenFilePaths.SaveDataFolderPath, $"test-results-{Scenario}.json"), sb.ToString());
            Log.Message("[LQVanilla] Results written; shutting down.");
            Root.Shutdown();
        }
    }

    /// <summary>
    /// Recording-profile helper: keeps the dev scene's ground weapons un-forbidden so the promo GIF
    /// needs only draft + undraft - after a kill, the enemy's dropped charge rifle (forbidden by
    /// vanilla on death) is cleared automatically, and POWQ can walk over and take it with no strip
    /// or un-forbid click. Guarded to maps holding our staged colonist (surname "Test"), so it never
    /// touches any other game.
    /// </summary>
    public class PowqDevUnforbidComponent : GameComponent
    {
        public PowqDevUnforbidComponent(Game game)
        {
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % 30 != 0)
            {
                return;
            }
            List<Map> maps = Find.Maps;
            for (int m = 0; m < maps.Count; m++)
            {
                Map map = maps[m];
                bool devScene = false;
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                {
                    if (p.Name is NameTriple nt && nt.Last == "Test")
                    {
                        devScene = true;
                        break;
                    }
                }
                if (!devScene)
                {
                    continue;
                }
                List<Thing> weapons = map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon);
                for (int i = 0; i < weapons.Count; i++)
                {
                    Thing w = weapons[i];
                    if (w.Spawned && w.IsForbidden(Faction.OfPlayer))
                    {
                        w.SetForbidden(false, warnOnFail: false);
                    }
                }
            }
        }
    }

    [StaticConstructorOnStartup]
    public static class LQVanillaBoot
    {
        static LQVanillaBoot()
        {
            Log.Message("[LQVanilla] assembly loaded.");
            // Suppress the default-ON scan until Arrange sets the stage, so no pre-arrange
            // think tick acts on the quicktest map's own scattered pawns.
            if (GenCommandLine.CommandLineArgPassed("lqvtest"))
            {
                PawnsOptimizeWeaponQualityMod.Settings.autoUpgrade = false;
            }
        }
    }
}
