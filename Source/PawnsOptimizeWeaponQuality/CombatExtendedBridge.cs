using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace PawnsOptimizeWeaponQuality
{
    /// <summary>Soft Combat Extended integration - reflection only, so this assembly carries no compile-time CE reference.</summary>
    internal static class CombatExtendedBridge
    {
        private static bool initialized;
        private static bool active;

        // Seam A: CE loadout trigger + cache lifecycle.
        private static MethodInfo loadoutJobGiver;    // JobGiver_UpdateLoadout.GetUpdateLoadoutJob(Pawn) -> Job
        private static MethodInfo getLoadout;         // Utility_Loadouts.GetLoadout(Pawn) -> Loadout
        private static FieldInfo defaultLoadoutField; // Loadout.defaultLoadout
        private static PropertyInfo slotsProp;        // Loadout.Slots -> IEnumerable<LoadoutSlot>
        private static PropertyInfo slotThingDefProp; // LoadoutSlot.thingDef -> ThingDef
        private static MethodInfo addClearCacheAction; // CacheClearComponent.AddClearCacheAction(Action)

        // Seam B: CE damage / inventory numbers.
        private static Type toolCeType;               // ToolCE
        private static MethodInfo getAdjustedDamage;  // StatWorker_MeleeDamageBase.GetAdjustedDamage(ToolCE, Thing) -> float
        private static Type compInventoryType;        // CompInventory
        private static MethodInfo updateInventory;    // CompInventory.UpdateInventory()

        /// <summary>True when Combat Extended is loaded and every bound member resolved.</summary>
        internal static bool Active
        {
            get
            {
                EnsureInit();
                return active;
            }
        }

        /// <summary>CE's loadout job-giver, for the CE-only patch's TargetMethod. Null when CE is absent.</summary>
        internal static MethodBase LoadoutJobGiverMethod
        {
            get
            {
                EnsureInit();
                return loadoutJobGiver;
            }
        }

        private static void EnsureInit()
        {
            if (initialized)
            {
                return;
            }
            initialized = true;
            try
            {
                Type giver = GenTypes.GetTypeInAnyAssembly("CombatExtended.JobGiver_UpdateLoadout");
                Type util = GenTypes.GetTypeInAnyAssembly("CombatExtended.Utility_Loadouts");
                Type loadout = GenTypes.GetTypeInAnyAssembly("CombatExtended.Loadout");
                Type slot = GenTypes.GetTypeInAnyAssembly("CombatExtended.LoadoutSlot");
                Type statWorker = GenTypes.GetTypeInAnyAssembly("CombatExtended.StatWorker_MeleeDamageBase");
                toolCeType = GenTypes.GetTypeInAnyAssembly("CombatExtended.ToolCE");
                compInventoryType = GenTypes.GetTypeInAnyAssembly("CombatExtended.CompInventory");
                Type cacheClear = GenTypes.GetTypeInAnyAssembly("CombatExtended.CacheClearComponent");

                if (giver == null || util == null || loadout == null || slot == null
                    || statWorker == null || toolCeType == null || compInventoryType == null)
                {
                    return; // CE not present: stay vanilla-only.
                }

                loadoutJobGiver = AccessTools.Method(giver, "GetUpdateLoadoutJob", new[] { typeof(Pawn) });
                getLoadout = AccessTools.Method(util, "GetLoadout", new[] { typeof(Pawn) });
                defaultLoadoutField = AccessTools.Field(loadout, "defaultLoadout");
                slotsProp = AccessTools.Property(loadout, "Slots");
                slotThingDefProp = AccessTools.Property(slot, "thingDef");
                getAdjustedDamage = AccessTools.Method(statWorker, "GetAdjustedDamage", new[] { toolCeType, typeof(Thing) });
                updateInventory = AccessTools.Method(compInventoryType, "UpdateInventory", Type.EmptyTypes);
                addClearCacheAction = cacheClear != null
                    ? AccessTools.Method(cacheClear, "AddClearCacheAction", new[] { typeof(Action) })
                    : null;

                active = loadoutJobGiver != null && getLoadout != null && defaultLoadoutField != null
                         && slotsProp != null && slotThingDefProp != null
                         && getAdjustedDamage != null && updateInventory != null;
                if (!active)
                {
                    Log.Warning(PowqGuard.LogPrefix + "Combat Extended is present but a bound member did not resolve; "
                                + "running vanilla-only. CE probably moved it.");
                }
            }
            catch (Exception e)
            {
                active = false;
                Log.Warning(PowqGuard.LogPrefix + "Combat Extended integration disabled (reflection threw); "
                            + "running vanilla-only. " + e);
            }
        }

        /// <summary>Weapon defs the pawn's CE loadout calls for (non-default loadouts only). Empty when CE is absent.</summary>
        internal static IEnumerable<ThingDef> LoadoutWeaponDefs(Pawn pawn)
        {
            EnsureInit();
            if (!active)
            {
                yield break;
            }
            object loadout = getLoadout.Invoke(null, new object[] { pawn });
            if (loadout == null || (bool)defaultLoadoutField.GetValue(loadout))
            {
                yield break;
            }
            if (!(slotsProp.GetValue(loadout) is IEnumerable slots))
            {
                yield break;
            }
            foreach (object slot in slots)
            {
                if (slot != null && slotThingDefProp.GetValue(slot) is ThingDef def && def.IsWeapon)
                {
                    yield return def;
                }
            }
        }

        /// <summary>CE's per-tool adjusted melee damage, summed over the weapon's ToolCE tools. Caller ensures Active.</summary>
        internal static float MeleeAdjustedDamage(Thing weapon)
        {
            List<Tool> tools = weapon.def.tools;
            if (tools == null)
            {
                return 0f;
            }
            float sum = 0f;
            foreach (Tool tool in tools)
            {
                if (toolCeType.IsInstanceOfType(tool))
                {
                    sum += (float)getAdjustedDamage.Invoke(null, new object[] { tool, weapon });
                }
            }
            return sum;
        }

        /// <summary>Resync CE's bulk/weight inventory tracking after a swap. No-op when CE is absent.</summary>
        internal static void SyncInventory(Pawn pawn)
        {
            EnsureInit();
            if (!active)
            {
                return;
            }
            List<ThingComp> comps = pawn.AllComps;
            if (comps == null)
            {
                return;
            }
            foreach (ThingComp comp in comps)
            {
                if (compInventoryType.IsInstanceOfType(comp))
                {
                    updateInventory.Invoke(comp, null);
                    return;
                }
            }
        }

        /// <summary>Register a callback with CE's cache-clear lifecycle. No-op when CE is absent.</summary>
        internal static void RegisterCacheClear(Action action)
        {
            EnsureInit();
            if (active && addClearCacheAction != null)
            {
                addClearCacheAction.Invoke(null, new object[] { action });
            }
        }
    }
}
