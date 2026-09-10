using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace PawnsOptimizeWeaponQuality
{
    /// <summary>CE path: postfix CE's loadout job-giver to swap loadout weapons for better map copies.</summary>
    [HarmonyPatch]
    public static class GetUpdateLoadoutJob_Patch
    {
        // Bound by reflection, so this assembly needs no compile-time CE reference; applied only when CE is present.
        public static bool Prepare() => CombatExtendedBridge.Active;

        public static MethodBase TargetMethod() => CombatExtendedBridge.LoadoutJobGiverMethod;

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            try
            {
                PostfixInner(pawn, ref __result);
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PowqGuard.LogPrefix + "Weapon-upgrade scan failed; CE's own loadout job stands. " + e, 0x0CE10001);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void PostfixInner(Pawn pawn, ref Job __result)
        {
            if (!Gate(pawn, __result))
            {
                return;
            }
            Job job = TryUpgradeJob(pawn, LoadoutTargets(pawn));
            if (job != null)
            {
                __result = job;
            }
        }

        /// <summary>Shared eligibility gate for both the CE and vanilla triggers.</summary>
        internal static bool Gate(Pawn pawn, Job existing)
        {
            if (existing != null || !PawnsOptimizeWeaponQualityMod.Settings.autoUpgrade)
            {
                return false;
            }
            if (pawn == null || !pawn.IsColonist || pawn.Downed || pawn.Drafted || pawn.Map == null)
            {
                return false;
            }
            // A recent swap changed nothing - back off, don't re-issue it every think tick.
            return !IsSidelined(pawn);
        }

        /// <summary>First worthwhile swap among the candidate (def, carried-instance) pairs, else null.</summary>
        internal static Job TryUpgradeJob(Pawn pawn, IEnumerable<(ThingDef def, ThingWithComps carried)> targets)
        {
            foreach ((ThingDef def, ThingWithComps carried) in targets)
            {
                if (carried == null)
                {
                    continue;
                }
                Thing best = FindBest(pawn, def, carried);
                if (best != null)
                {
                    return JobMaker.MakeJob(PowqDefOf.POWQ_SwapWeapon, best, carried);
                }
            }
            return null;
        }

        // CE trigger: the weapons the pawn's loadout calls for, matched to the instance it currently carries.
        private static IEnumerable<(ThingDef, ThingWithComps)> LoadoutTargets(Pawn pawn)
        {
            foreach (ThingDef def in CombatExtendedBridge.LoadoutWeaponDefs(pawn))
            {
                yield return (def, CarriedInstance(pawn, def));
            }
        }

        // Vanilla trigger: every weapon the pawn currently holds (equipped primary + carried), matched to itself.
        internal static IEnumerable<(ThingDef, ThingWithComps)> HeldTargets(Pawn pawn)
        {
            ThingWithComps primary = pawn.equipment?.Primary;
            if (primary != null && primary.def.IsWeapon)
            {
                yield return (primary.def, primary);
            }
            IEnumerable<ThingWithComps> carried = pawn.inventory?.innerContainer?.OfType<ThingWithComps>();
            if (carried != null)
            {
                foreach (ThingWithComps twc in carried)
                {
                    if (twc.def.IsWeapon)
                    {
                        yield return (twc.def, twc);
                    }
                }
            }
        }

        /// <summary>The best map copy of <paramref name="def"/> that beats the carried
        /// one on the class-appropriate key.</summary>
        private static Thing FindBest(Pawn pawn, ThingDef def, ThingWithComps carried)
        {
            // Never propose shedding a weapon a quest requires this lodger to keep.
            if (IsQuestLocked(pawn, carried))
            {
                return null;
            }
            bool ranged = def.IsRangedWeapon;
            QualityCategory floor = PawnsOptimizeWeaponQualityMod.Settings.minQuality;
            Thing best = carried;
            foreach (Thing candidate in pawn.Map.listerThings.ThingsOfDef(def))
            {
                if (!candidate.Spawned || candidate.IsForbidden(pawn) || candidate.IsBurning())
                {
                    continue;
                }
                if (!candidate.TryGetQuality(out QualityCategory q) || q < floor)
                {
                    continue; // global floor - never acquire below this quality
                }
                if (ranged && def.MadeFromStuff && candidate.Stuff != carried.Stuff)
                {
                    continue; // stuffed gun: no callable DPS to compare across materials
                }
                if (!Beats(candidate, best, ranged))
                {
                    continue;
                }
                // The pawn must actually be allowed to wield it.
                if (!EquipmentUtility.CanEquip(candidate, pawn))
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

        /// <summary>Strict-better on the class key, hit-point bucket as tiebreak.</summary>
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
                // The vanilla abstract key is quality-blind, so quality breaks its ties.
                // (CE's key already folds quality, so under CE this is moot and skipped.)
                if (!CombatExtendedBridge.Active)
                {
                    QualityCategory qc = QualityOf(candidate);
                    QualityCategory qi = QualityOf(incumbent);
                    if (qc != qi)
                    {
                        return qc > qi;
                    }
                }
            }
            return HpBucket(candidate) > HpBucket(incumbent);
        }

        /// <summary>Melee ranking key: CE's per-tool adjusted damage when CE is loaded, else the weapon's
        /// wielder-free ABSTRACT average DPS. Vanilla's instance stat folds the holder's melee-damage factor
        /// into an EQUIPPED weapon, which would let an impaired pawn ping-pong its melee primary against a
        /// ground copy; the abstract form has no holder term (but is quality-blind - Beats breaks ties on quality).</summary>
        private static float MeleeDps(Thing t)
        {
            return CombatExtendedBridge.Active
                ? CombatExtendedBridge.MeleeAdjustedDamage(t)
                : t.def.GetStatValueAbstract(StatDefOf.MeleeWeapon_AverageDPS, t.Stuff);
        }

        // Vanilla quest-lodger lock (what CE's IsItemQuestLocked composes for weapons): a lodger
        // may not shed a weapon a quest requires them to keep.
        private static bool IsQuestLocked(Pawn pawn, Thing weapon)
            => QuestUtility.IsQuestLodger(pawn) && !EquipmentUtility.QuestLodgerCanUnequip(weapon, pawn);

        private static QualityCategory QualityOf(Thing t)
            => t.TryGetQuality(out QualityCategory q) ? q : QualityCategory.Awful;

        private static int HpBucket(Thing t)
        {
            int size = PawnsOptimizeWeaponQualityMod.Settings.hpBucket;
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

        // Per-pawn back-off.
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

        // Clear because this static lives for the whole process.
        internal static void ClearSidelines() => sidelinedUntil.Clear();
    }

    /// <summary>Vanilla path (no CE): postfix apparel-optimization to swap held weapons for better map copies.</summary>
    [HarmonyPatch(typeof(JobGiver_OptimizeApparel), "TryGiveJob", new[] { typeof(Pawn) })]
    public static class OptimizeApparelUpgrade_Patch
    {
        // Only when CE is absent; under CE the loadout postfix owns upgrades and this stays uninstalled.
        public static bool Prepare() => !CombatExtendedBridge.Active;

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, ref Job __result)
        {
            try
            {
                if (!GetUpdateLoadoutJob_Patch.Gate(pawn, __result))
                {
                    return;
                }
                Job job = GetUpdateLoadoutJob_Patch.TryUpgradeJob(pawn, GetUpdateLoadoutJob_Patch.HeldTargets(pawn));
                if (job != null)
                {
                    __result = job;
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce(PowqGuard.LogPrefix + "Weapon-upgrade scan failed; the apparel job stands. " + e, 0x0CE10003);
            }
        }
    }

    [DefOf]
    public static class PowqDefOf
    {
        public static JobDef POWQ_SwapWeapon;

        static PowqDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(PowqDefOf));
        }
    }

    /// <summary>Swaps a pawn's carried or equipped weapon for a better copy on the map.</summary>
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
            swap.initAction = () =>
            {
                bool swapped = false;
                try
                {
                    swapped = DoSwap();
                }
                catch (Exception e)
                {
                    Log.ErrorOnce(PowqGuard.LogPrefix + "Weapon swap failed mid-job; the pawn keeps its weapon. " + e, 0x0CE10002);
                }
                if (!swapped)
                {
                    GetUpdateLoadoutJob_Patch.SidelineAfterFailure(pawn);
                }
            };
            swap.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return swap;
        }

        /// <summary>Performs the swap.</summary>
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
                    if (!pawn.equipment.TryDropEquipment(oldWeapon, out ThingWithComps droppedEq, pawn.Position, forbid: false))
                    {
                        return false;
                    }
                    dropped = droppedEq;
                }
                else if (pawn.inventory.innerContainer.Contains(oldWeapon))
                {
                    // Abort rather than add the new one on top of an undropped old one.
                    if (!pawn.inventory.innerContainer.TryDrop(oldWeapon, pawn.Position, pawn.Map,
                            ThingPlaceMode.Near, out dropped))
                    {
                        return false;
                    }
                }
                // Don't forbid dropped weapon.
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
                // Could not stow it - put it back on the map rather than lose it.
                if (!newWeapon.Spawned)
                {
                    GenPlace.TryPlaceThing(newWeapon, pawn.Position, pawn.Map, ThingPlaceMode.Near);
                }
                return false;
            }
            CombatExtendedBridge.SyncInventory(pawn);
            SidearmsBridge.NotifySwap(pawn, oldWeapon, newWeapon);
            return true;
        }
    }

    /// <summary>Soft Simple Sidearms integration.</summary>
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
                // SS doesn't store instance or quality in its memory, so need to
                // tell SS when the pair actually changes.
                var oldTwc = oldWeapon as ThingWithComps;
                bool samePair = oldTwc != null
                    && oldTwc.def == newWeapon.def && oldTwc.Stuff == newWeapon.Stuff;
                if (samePair)
                {
                    return;
                }
                if (informDropped != null && oldTwc != null)
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
                // SS absent or API drifted - soft integration stays soft.
            }
        }
    }
}
