using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using CombatExtended;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace LoadoutQuality
{
    /// <summary>
    /// Auto weapon upgrade. No parallel scheduler: a postfix on CE's own
    /// GetUpdateLoadoutJob — when CE has no loadout work for the pawn, scan the
    /// loadout-declared weapons for the BEST available copy of the same def on the
    /// map and, if it beats what the pawn carries, return a swap job. Free-rides
    /// CE's 1800-tick throttle, think priority, and the Assign tab's update-now
    /// button.
    ///
    /// "Best" is class-appropriate, because a CE loadout names a weapon def but not
    /// its material:
    ///   - ranged: highest quality, then hit-point bucket. Guns are not made from
    ///     stuff, so quality is the whole ordering; a modded stuffable gun is held to
    ///     its own material (there is no callable ranged-DPS to rank guns across
    ///     materials without reproducing CE's formula — which we will not do).
    ///   - melee: highest MeleeWeapon_AverageDPS, then hit-point bucket. That is
    ///     vanilla's own stat and it already folds material AND quality, so a plasteel
    ///     copy rightly beats a higher-quality steel one. We CALL the stat; we do not
    ///     recompute it.
    /// A global quality floor (settings) gates every candidate regardless of class.
    ///
    /// Selection is global-max, not nearest-better: the pawn reroutes at most once,
    /// straight to the best copy, never up a staircase of intermediate piles.
    /// </summary>
    [HarmonyPatch(typeof(JobGiver_UpdateLoadout), nameof(JobGiver_UpdateLoadout.GetUpdateLoadoutJob))]
    public static class GetUpdateLoadoutJob_Patch
    {
        public static bool Prepare() => LQGuard.Require(typeof(JobGiver_UpdateLoadout),
            nameof(JobGiver_UpdateLoadout.GetUpdateLoadoutJob), new[] { typeof(Pawn) },
            "the auto weapon-upgrade feature is disabled (pawns keep whatever quality they first grab).");

        // Thin outer / NoInlining inner (failure-doctrine layer 3): the inner body
        // references CE members the JIT resolves only when it first compiles — a CE
        // rename would throw from inside the hook, uncatchable by the Prepare guard;
        // the try here turns that into a one-time error, CE's own job intact.
        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            try
            {
                PostfixInner(pawn, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(LQGuard.LogPrefix + "Weapon-upgrade scan failed; CE's own loadout job stands. " + e, 0x0CE10001);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, ref Job __result)
        {
            if (__result != null || !LoadoutQualityMod.Settings.autoUpgrade)
            {
                return;
            }
            if (pawn == null || !pawn.IsColonist || pawn.Downed || pawn.Drafted || pawn.Map == null)
            {
                return;
            }
            Loadout loadout = pawn.GetLoadout();
            if (loadout == null || loadout.defaultLoadout)
            {
                return;
            }
            if (IsSidelined(pawn))
            {
                return; // a recent swap failed to change anything — back off, don't re-issue it every tick
            }

            foreach (LoadoutSlot slot in loadout.Slots)
            {
                if (slot.thingDef == null || !slot.thingDef.IsWeapon)
                {
                    continue;
                }
                ThingWithComps carried = CarriedInstance(pawn, slot.thingDef);
                if (carried == null)
                {
                    continue;
                }
                Thing best = FindBest(pawn, slot.thingDef, carried);
                if (best != null)
                {
                    __result = JobMaker.MakeJob(LQDefOf.LQ_SwapWeapon, best, carried);
                    return;
                }
            }
        }

        /// <summary>The best map copy of <paramref name="def"/> that beats the carried
        /// one on the class-appropriate key, or null when nothing does. Cheap tests
        /// gate before the expensive reserve/reach, and a beaten-but-unreachable copy
        /// is skipped in favour of the best REACHABLE one.</summary>
        private static Thing FindBest(Pawn pawn, ThingDef def, ThingWithComps carried)
        {
            bool ranged = def.IsRangedWeapon;
            QualityCategory floor = LoadoutQualityMod.Settings.minQuality;
            Thing best = carried;
            foreach (Thing candidate in pawn.Map.listerThings.ThingsOfDef(def))
            {
                if (!candidate.Spawned || candidate.IsForbidden(pawn) || candidate.IsBurning())
                {
                    continue;
                }
                if (!candidate.TryGetQuality(out QualityCategory q) || q < floor)
                {
                    continue; // global floor — never acquire below this quality
                }
                if (ranged && def.MadeFromStuff && candidate.Stuff != carried.Stuff)
                {
                    continue; // stuffed gun: no callable DPS to compare across materials
                }
                if (!Beats(candidate, best, ranged))
                {
                    continue;
                }
                if (!pawn.CanReserveAndReach(candidate, PathEndMode.ClosestTouch, Danger.None))
                {
                    continue;
                }
                best = candidate;
            }
            return best != carried ? best : null;
        }

        /// <summary>Strict-better on the class key, hit-point bucket as tiebreak.
        /// Strictness in both directions guarantees termination: once the pawn holds
        /// the winner, no equal-or-worse copy can win it back, so the swap never
        /// ping-pongs. Ranged ties on quality then on HP bucket; melee ties on
        /// wielder-independent DPS (see MeleeDps) then on HP bucket.</summary>
        private static bool Beats(Thing candidate, Thing incumbent, bool ranged)
        {
            if (ranged)
            {
                QualityCategory qc = QualityOf(candidate);
                QualityCategory qi = QualityOf(incumbent);
                if (qc != qi)
                {
                    return qc > qi;
                }
            }
            else
            {
                float dc = MeleeDps(candidate);
                float di = MeleeDps(incumbent);
                if (dc != di)
                {
                    return dc > di;
                }
            }
            return HpBucket(candidate) > HpBucket(incumbent);
        }

        /// <summary>Melee DPS measured WIELDER-INDEPENDENTLY, from def+material+quality
        /// alone. MeleeWeapon_AverageDPS on an instance folds in the wielder's melee
        /// factors when (and only when) the weapon is EQUIPPED — the stat worker reads
        /// the weapon's holder — so comparing an equipped incumbent's value against a
        /// ground candidate's would compare wielded DPS against bare DPS and break the
        /// determinism the termination proof rests on. Requesting the stat abstractly
        /// (no thing, hence no holder) gives every weapon the same basis; the pawn's
        /// factors are a constant multiplier across all its weapons, so dropping them
        /// leaves the RANKING unchanged. The stat is still called, never recomputed.</summary>
        private static float MeleeDps(Thing t)
        {
            t.TryGetQuality(out QualityCategory q);
            return StatDefOf.MeleeWeapon_AverageDPS.Worker.GetValue(
                StatRequest.For(t.def, t.Stuff, q));
        }

        private static QualityCategory QualityOf(Thing t)
            => t.TryGetQuality(out QualityCategory q) ? q : QualityCategory.Awful;

        private static int HpBucket(Thing t)
        {
            int size = LoadoutQualityMod.Settings.hpBucket;
            return t.HitPoints / (size > 0 ? size : 1);
        }

        private static ThingWithComps CarriedInstance(Pawn pawn, ThingDef def)
        {
            if (pawn.equipment?.Primary?.def == def)
            {
                return pawn.equipment.Primary;
            }
            return pawn.inventory?.innerContainer?.OfType<ThingWithComps>()
                .FirstOrDefault(t => t.def == def);
        }

        // Per-pawn back-off. A swap that failed to change the carried weapon (a caught
        // structural failure, or no cell to set the old weapon down) is a persistent
        // no-op: because our non-null result makes CE's throttle keep the pawn
        // immediately re-eligible, the identical swap would re-issue every think tick —
        // a walk-loop that starves real work. Sidelining the pawn's upgrades for a
        // spell lets a persistent failure self-throttle, then retry (the obstruction
        // may have cleared) instead of looping.
        private const int SidelineTicks = 2500;
        private static readonly Dictionary<int, int> sidelinedUntil = new Dictionary<int, int>();

        internal static void SidelineAfterFailure(Pawn pawn)
        {
            if (pawn == null)
            {
                return;
            }
            int now = GenTicks.TicksGame;
            if (sidelinedUntil.Count > 64)
            {
                foreach (int id in sidelinedUntil.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                {
                    sidelinedUntil.Remove(id);
                }
            }
            sidelinedUntil[pawn.thingIDNumber] = now + SidelineTicks;
        }

        private static bool IsSidelined(Pawn pawn)
        {
            return sidelinedUntil.TryGetValue(pawn.thingIDNumber, out int until)
                   && GenTicks.TicksGame < until;
        }
    }

    [DefOf]
    public static class LQDefOf
    {
        public static JobDef LQ_SwapWeapon;

        static LQDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(LQDefOf));
        }
    }

    /// <summary>
    /// TargetA = better weapon on the map, TargetB = carried weapon it replaces.
    /// Reservation (not map-wide forbid hacks) prevents pickup races; the old weapon
    /// drops IN PLACE, UNFORBIDDEN — vanilla JobDriver_OptimizeApparel convention:
    /// haulers store it, other pawns may legitimately claim it. Simple Sidearms, when
    /// present, is informed via reflection so its memory tracks the new instance.
    /// </summary>
    public class JobDriver_SwapWeapon : JobDriver
    {
        private Thing NewWeapon => job.targetA.Thing;
        private Thing OldWeapon => job.targetB.Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(NewWeapon, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TargetIndex.A);
            this.FailOnBurningImmobile(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(TargetIndex.A);
            Toil swap = ToilMaker.MakeToil("LQ_Swap");
            // The swap reaches CE's CompInventory and (soft) SS members the JIT
            // resolves on first compile — outside any Prepare guard — so it carries
            // its own failure-doctrine try: a CE/SS rename becomes one logged error
            // and a no-op, not a raw job exception spamming the log.
            swap.initAction = () =>
            {
                bool swapped = false;
                try
                {
                    swapped = DoSwap();
                }
                catch (Exception e)
                {
                    Log.ErrorOnce(LQGuard.LogPrefix + "Weapon swap failed mid-job; the pawn keeps its weapon. " + e, 0x0CE10002);
                }
                if (!swapped)
                {
                    GetUpdateLoadoutJob_Patch.SidelineAfterFailure(pawn);
                }
            };
            swap.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return swap;
        }

        /// <summary>Performs the swap. Returns true when the pawn ends up holding the
        /// new weapon (or there was nothing to do); false when the swap changed
        /// nothing, so the caller can sideline the pawn instead of re-issuing it.
        /// Loss-proof: the old weapon is only removed once we know the new one can take
        /// its place, and the new weapon is never left despawned-and-unowned.</summary>
        private bool DoSwap()
        {
            var newWeapon = (ThingWithComps)NewWeapon;
            var oldWeapon = OldWeapon as ThingWithComps;
            if (newWeapon == null || newWeapon.Destroyed)
            {
                return true; // the winner is gone; nothing to retry
            }
            bool wasEquipped = pawn.equipment?.Primary == oldWeapon;
            Thing dropped = null;
            if (oldWeapon != null && !oldWeapon.Destroyed)
            {
                if (wasEquipped)
                {
                    // Clear the primary slot BEFORE taking the new weapon off the map.
                    // TryDropEquipment returns false when no cell can receive the drop;
                    // proceeding would despawn the new weapon into a slot AddEquipment
                    // then refuses (it logs and no-ops) — destroying the upgrade.
                    if (!pawn.equipment.TryDropEquipment(oldWeapon, out ThingWithComps droppedEq, pawn.Position, forbid: false))
                    {
                        return false;
                    }
                    dropped = droppedEq;
                }
                else if (pawn.inventory.innerContainer.Contains(oldWeapon))
                {
                    pawn.inventory.innerContainer.TryDrop(oldWeapon, pawn.Position, pawn.Map,
                        ThingPlaceMode.Near, out dropped);
                }
                // Other mods (e.g. Simple Sidearms' drop handling) may forbid dropped
                // weapons; the replaced copy is a hand-me-down for haulers and other
                // pawns — explicitly leave it claimable.
                dropped?.SetForbidden(false, warnOnFail: false);
            }
            if (newWeapon.Spawned)
            {
                newWeapon.DeSpawn();
            }
            if (wasEquipped)
            {
                pawn.equipment.AddEquipment(newWeapon); // slot cleared above, so this takes
            }
            else if (!pawn.inventory.innerContainer.TryAdd(newWeapon, true))
            {
                // Could not stow it — put it back on the map rather than lose it.
                if (!newWeapon.Spawned)
                {
                    GenPlace.TryPlaceThing(newWeapon, pawn.Position, pawn.Map, ThingPlaceMode.Near);
                }
                return false;
            }
            pawn.TryGetComp<CompInventory>()?.UpdateInventory();
            SidearmsBridge.NotifySwap(pawn, oldWeapon, newWeapon);
            return true;
        }
    }

    /// <summary>Soft Simple Sidearms integration — reflection only, no reference.</summary>
    public static class SidearmsBridge
    {
        private static bool initialized;
        private static MethodInfo getMemory;
        private static MethodInfo informAdded;
        private static MethodInfo informDropped;

        public static void NotifySwap(Pawn pawn, Thing oldWeapon, Thing newWeapon)
        {
            if (!initialized)
            {
                initialized = true;
                Type memoryType = GenTypes.GetTypeInAnyAssembly("SimpleSidearms.rimworld.CompSidearmMemory");
                if (memoryType != null)
                {
                    getMemory = memoryType.GetMethod("GetMemoryCompForPawn",
                        BindingFlags.Public | BindingFlags.Static);
                    informAdded = memoryType.GetMethod("InformOfAddedSidearm",
                        BindingFlags.Public | BindingFlags.Instance);
                    informDropped = memoryType.GetMethod("InformOfDroppedSidearm",
                        BindingFlags.Public | BindingFlags.Instance);
                }
            }
            if (getMemory == null || newWeapon == null)
            {
                return;
            }
            try
            {
                object memory = getMemory.Invoke(null, new object[] { pawn, true });
                if (memory == null)
                {
                    return;
                }
                // Symmetric: forget the old copy (intentional drop — do not re-acquire
                // it) AND remember the new one, so SS memory tracks the upgrade rather
                // than a phantom pair.
                if (informDropped != null && oldWeapon is ThingWithComps oldTwc)
                {
                    informDropped.Invoke(memory, new object[] { oldTwc, true });
                }
                if (informAdded != null)
                {
                    informAdded.Invoke(memory, new object[] { newWeapon });
                }
            }
            catch
            {
                // SS absent or API drifted — soft integration stays soft.
            }
        }
    }
}
