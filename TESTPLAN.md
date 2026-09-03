# Test plan — Loadout Quality for Combat Extended

Automated end-to-end in this repo's own **CE-only profile**
(Core + Harmony + Combat Extended + this mod — no Simple Sidearms, no suite;
that's the "works with CE alone" claim):

```
./test/run-lq-stage.sh                    # regenerate the LQ saves (quits after the letter)
./test/run-lq-assert.sh lq1 LQ-1-ranged
./test/run-lq-assert.sh lq2 LQ-2-melee
./test/run-lq-assert.sh lq3 LQ-3-bucket
./test/run-lq-assert.sh lq4 LQ-4-floor
./test/run-lq-assert.sh lq5 LQ-5-meleeprime
```

Results: `test/SaveData/test-results-lq*.json`. Green (12/12 checks) recorded
2026-09-03:

- **lq1 (ranged)** — equipped-weapon path. Phase 0 (toggle OFF): a Normal rifle is
  retained for 1800 ticks (install-consent negative control). Phase 1 (ON): the
  pawn swaps to the Excellent map copy and the Normal one lies dropped UNFORBIDDEN
  at its feet.
- **lq2 (melee, inventory sidearm) — HP-NEUTRAL.** A rifle holds the primary slot so
  a steel gladius stays an INVENTORY sidearm (inventory swap branch). Every gladius
  is at full HP, so the HP-bucket tiebreak is neutral and the swap can only be driven
  by melee DPS. Phase 0 (same material): steel Normal → steel Excellent proves DPS
  folds QUALITY. Phase 1 (same quality): steel Excellent → plasteel Excellent proves
  DPS folds MATERIAL. Old copy dropped unforbidden. **This replaces an earlier lq2
  that was green for the wrong reason** — it carried steel Normal and staged plasteel
  Excellent, and plasteel sits in a higher HP bucket, so the swap fired on HP, never
  on DPS; it passed even while melee ranking was quality/material-blind (see round 2).
- **lq3 (HP-bucket tiebreak)** — carried Excellent rifle damaged to a low bucket.
  Phase 0: an Excellent rifle at the SAME bucket must NOT tempt a swap (1800 ticks,
  anti-thrash). Phase 1: a full-HP Excellent rifle (higher bucket) wins.
- **lq4 (min-quality floor)** — floor raised to Good, carried Awful rifle. Phase 0: a
  Poor rifle (better than Awful, below the floor) is REFUSED, Awful retained. Phase 1:
  an Excellent rifle (above the floor) is acquired.
- **lq5 (melee EQUIPPED-PRIMARY, skill 4)** — the only case CE's melee-DPS stat skews.
  A steel Excellent gladius equipped as primary; melee skill 4 (so CE's variation
  factor f<1). Phase 0: an IDENTICAL steel Excellent ground copy must NOT be swapped to
  (the old equipped-skewed ranking made it out-measure the equipped weapon → downgrade
  + ping-pong; fingerprinted by instance id). Phase 1: a plasteel Excellent copy
  (material) must win — proving equipped-primary melee still upgrades correctly.

## Adversarial round 1 (2026-09-03) — 3 attackers, 3 fixes

Over the post-rewrite state (13be4d7). Three real defects, all fixed (30f4284):
HIGH equipped-swap weapon-loss (clear-slot-before-despawn / abort / re-place);
MEDIUM throttle walk-loop (per-pawn sideline cooldown); a melee-DPS wielder concern
(see round 2 — it was corrected). Plus SS-bridge symmetry.

## Convergence round 2 (2026-09-03) — whole-repo, 3 attackers, 5 fixes

Ran on the whole current repo (prove converged state, not just the diff). It found
that the *round-1 melee fix was itself a regression*, plus more — all fixed:

- **[HIGH] Melee was ranked on hit points alone.** CE REPLACES the
  MeleeWeapon_AverageDPS worker (`PatchOperationReplace` → CE's
  `StatWorker_MeleeDamageAverage`), and CE's worker reads quality+material off the
  passed Thing. Round 1's "wielder-asymmetry" was analysed against VANILLA's worker,
  which CE does not use — so the fix (an ABSTRACT `StatRequest.For(def,stuff,q)`)
  hit CE's `ownerEquipment==null` branch and collapsed to the tool's raw power: a
  per-def CONSTANT, blind to quality and material. Every melee comparison tied on DPS
  and fell to the HP bucket — a pawn could DOWNGRADE a Masterwork to an Awful full-HP
  copy. Fix: revert `MeleeDps` to the INSTANCE path (`t.GetStatValue(...)`), which
  under CE folds quality and material correctly. The prior lq2 passed only because
  plasteel outranks steel on HP bucket — rebuilt HP-neutral to actually exercise DPS.
  **Accepted residual:** CE scales an EQUIPPED-PRIMARY weapon's DPS by the wielder's
  melee-skill damage variation (ground candidates and inventory sidearms are
  unaffected), a small skew when a pawn's MAIN weapon is melee; normalising it out
  would reproduce CE's variation formula (a mirror), so it is documented not mirrored.
- **[MEDIUM] SS same-pair flag clobber** (round-1 fix #4 regressed). SS keys sidearms
  by def+material pair; informing a drop+add of the SAME pair drops its remembered
  count to zero between the calls, and `ForgetSidearmMemory` then nulls the player's
  forced/preferred/default flag. Fix: only inform SS when the pair actually changes
  (SS does not observe our raw drop/add, so a same-pair swap needs no update).
- **[MEDIUM] Missing equip/drop legality gates.** `FindBest` didn't call
  `EquipmentUtility.CanEquip`, and `DoSwap` didn't check `IsItemQuestLocked` — CE's
  own pickup validator and drop path check both, so LQ could equip a
  biocoded/persona/role-locked weapon or shed a quest-locked one. Fix: added both
  gates (calls, not reproductions).
- **[LOW] `sidelinedUntil` never cleared on load** — a stale high-tick entry could
  mis-gate a pawn after loading an earlier save or a second colony. Fix: registered
  `CacheClearComponent.AddClearCacheAction` (CE's own hook, as CE does for _throttle).
- **[LOW] Inventory failed-drop double-hold** — a failed inventory TryDrop then added
  the new weapon anyway, leaving the pawn holding both. Fix: abort (sideline) instead.

Accepted / code-reviewed (not e2e-pinned, as staging them reliably isn't tractable):
the equipped despawn-before-add orphan on an exotic throwing `Notify_Equipped`; the
two lossy DoSwap edges under pathological placement; the sideline cooldown; the
CanEquip/quest-lock gates (no SS/biocode content in the CE-only profile); the SS
same-pair fix (no SS in the profile). All verified against decompiles.

## Confirmation round 3 (2026-09-03) — 2 attackers, 1 fix (the melee key)

Confirmed round 2's five fixes: the gates (CanEquip, quest-lock, inventory-abort,
cache-clear) and the SS same-pair short-circuit all CLEAN (CanEquip allows
self-bonded and rejects only genuinely-unequippable weapons; quest-lock only ever
fires for lodgers; the inventory-abort is loss-safe; cache-clear fires only at game
load). BUT both attackers flagged that round-2's melee revert to the instance path
had re-opened a termination hole under-rated in the ledger as a "small residual":

- **[HIGH] Equipped-primary melee ping-pong / downgrade below melee skill 10.** CE's
  worker multiplies an EQUIPPED weapon's DPS by f = 0.75 + 0.025·skill (ground copies
  and inventory sidearms get 1.0). For skill < 10 (f < 1) the equipped weapon is
  under-valued, so an equal-or-worse ground copy out-measures it — even an IDENTICAL
  one (D > f·D). The pawn downgrades, drops the loser unforbidden at its feet, and
  swaps back next tick; self-seeded by the mod's own upgrades, and the sideline can't
  catch it (each swap succeeds). Fix: rank melee by CE's
  `StatWorker_MeleeDamageBase.GetAdjustedDamage(tool, thing)` summed over the def's
  tools — it folds quality AND material with NO wielder term, so equipped and ground
  copies share one basis. A call to CE's damage, not a reproduction of its DPS formula
  (`MeleeWeapon_DamageMultiplier` was ruled out — it folds quality only). Documented
  edge: for a weapon whose tools mix sharp+blunt damage the material factor isn't
  uniform across tools, so an unweighted sum can misorder a same-quality pair by a
  sub-percent margin. Most Core melee weapons DO mix types (Poke/handle are blunt), but
  no vanilla weapon+material combination actually reorders under this key (brute-force
  verified — no vanilla material offers the blunt-up/sharp-down tradeoff a flip needs);
  the real exposure is a modded weapon/stuff of extreme profile, and even then it can't
  ping-pong or crash. lq5 pins the fix.

## Harness findings (kept from prior rounds)

- **The suite profile contaminates LQ scenarios**: the Loadouts module + SS logistics
  fetched quality-preferred instances and re-forbade drops. LQ tests live in a CE-only
  profile — also the honest test of the "works with CE alone" claim. (The former
  overlap with the compat suite's P10 is gone — the rewrite dropped LQ's
  GetExcessEquipment patch.)
- **Race**: a save that loads unpaused ticks before LoadedGame callbacks — the
  toggle-off reset lost to the think tree once. Settings are killed in the test
  assembly's static constructor (mod-init), before any save can tick.
- **Quicktest maps scatter random weapons**, but map-gen loose weapons are FORBIDDEN,
  so LQ's scan skips them; staged ground pieces are spawned unforbidden and are the
  only candidates. Assertions still search pawn-adjacent instances only.
- **Spurious FileNotFound** on a provably-valid save was gamescope boot contention
  from killing the stage and relaunching an assert too fast — settles between launches
  fixed it.
- Vanilla auto-forbids drops outside the home area — the staging paints a home area
  over the scene so the dropped-unforbidden contract is testable.
