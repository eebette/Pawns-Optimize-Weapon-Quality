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
```

Results: `test/SaveData/test-results-lq*.json`. Green (9/9 checks) recorded
2026-09-03:

- **lq1 (ranged)** — equipped-weapon path. Phase 0 (toggle OFF): a Normal rifle
  is retained for 1800 ticks (install-consent negative control). Phase 1 (ON):
  the pawn swaps to the Excellent map copy and the Normal one lies dropped
  UNFORBIDDEN at its feet.
- **lq2 (melee, inventory sidearm)** — a rifle holds the primary slot so a steel
  Normal gladius stays an INVENTORY sidearm (exercises the inventory swap
  branch). Ranked by MeleeWeapon_AverageDPS, which folds material, so a PLASTEEL
  Excellent gladius on the ground beats it — the pawn swaps, steel dropped
  unforbidden. A/B: under a quality-then-same-material rule the plasteel copy is
  a different material and never selected — the check goes RED, pinning the DPS
  design.
- **lq3 (HP-bucket tiebreak)** — carried Excellent rifle damaged to a low bucket.
  Phase 0: an Excellent rifle at the SAME bucket must NOT tempt a swap (1800
  ticks, anti-thrash). Phase 1: a full-HP Excellent rifle (higher bucket) wins.
- **lq4 (min-quality floor)** — floor raised to Good, carried Awful rifle. Phase
  0: a Poor rifle (better than Awful, below the floor) is REFUSED, Awful
  retained. Phase 1: an Excellent rifle (above the floor) is acquired.

## Adversarial review round (2026-09-03) — 3 attackers, 3 fixes

Three independent attackers over the post-rewrite state (commit 13be4d7). The
mod looked converged; three real defects surfaced, all fixed:

- **[HIGH] Equipped swap could destroy the upgrade.** `DoSwap` removed the old
  weapon then despawned+added the new WITHOUT checking the drop succeeded. If
  `TryDropEquipment` finds no cell (a boxed-in pawn or a packed stockpile —
  exactly where upgrade weapons accumulate), the old stays equipped, and
  `AddEquipment` into an occupied primary LOGS-and-no-ops (it does not throw, so
  the toil's try never fired) — the despawned new weapon is orphaned and GC'd.
  Fix: clear the slot BEFORE taking the new weapon off the map; abort the swap on
  a failed drop; on a failed inventory add, re-place the new weapon rather than
  lose it. `DoSwap` now returns success.
- **[MEDIUM] Throttle walk-loop on a persistent no-op.** Our non-null result
  makes CE set `_throttle = now-1801` (immediate re-eligibility), so termination
  depends on the swap actually changing the carried weapon. A persistent no-op —
  the HIGH's drop-abort, or a caught CE-rename exception in `DoSwap` — re-issued
  the identical swap every think tick, starving work. Fix: a per-pawn cooldown
  (`SidelineAfterFailure`/`IsSidelined`, 2500 ticks) — a swap that changed
  nothing sidelines the pawn's upgrades briefly, so a persistent failure
  self-throttles and later retries instead of looping.
- **[LOW → real] Melee DPS wielder-asymmetry.** `MeleeWeapon_AverageDPS` on an
  EQUIPPED instance folds in the wielder's melee factors (the stat worker reads
  the weapon's holder); a ground candidate has none. Comparing the two mixed
  wielded-vs-bare DPS and broke the determinism the termination proof rests on —
  dormant in vanilla (factors ≡ 1 for armed adult humans) but live under common
  modded melee-stat content (genes/hediffs/drugs/apparel), where it could skip a
  genuine upgrade or ping-pong. Fix: `MeleeDps` requests the stat abstractly
  (def+material+quality, no holder), so incumbent and candidates share one basis;
  the pawn's factors are a constant multiplier that does not change the ranking.
  Still a call, not a reproduction.
- Also: the SS reflection bridge now informs SS of the DROPPED old sidearm
  (intentional) as well as the added new one, so SS memory tracks the upgrade
  instead of a phantom pair.

The attackers CLEARED (verified against decompiles): global-max termination and
no ping-pong (strict-better both directions), the CE throttle interplay in the
normal case, the guard set (prisoners/caravan/off-map/mental-state all excluded),
the failure doctrine (per-class bootstrap, Prepare guard, NoInlining split,
distinct error keys), the settings edges (0/negative HP bucket neutralized), and
the no-mirror contract. The two lossy failure paths (HIGH abort, LOW inventory
re-place) are reachable only under pathological placement / add-failing content
and are code-reviewed + logic-proven rather than e2e-pinned (staging a
no-drop-cell reliably is not tractable); the cooldown likewise.

## Harness findings (kept from prior rounds)

- **The suite profile contaminates LQ scenarios**: the Loadouts module + SS's own
  logistics fetched quality-preferred instances and re-forbade drops. LQ tests
  live in a CE-only profile — also the honest test of the "works with CE alone"
  claim. (The former overlap with the compat suite's P10 no longer exists — the
  rewrite dropped LQ's GetExcessEquipment patch.)
- **Race**: a save that loads unpaused ticks before LoadedGame callbacks — the
  toggle-off reset lost to the think tree once. Settings are killed in the test
  assembly's static constructor (mod-init), before any save can tick.
- **Quicktest maps scatter random weapons**, but map-gen loose weapons are
  FORBIDDEN, so LQ's scan skips them; staged ground pieces are spawned
  unforbidden and are the only candidates. Assertions still search pawn-adjacent
  instances only.
- Vanilla auto-forbids drops outside the home area — the staging paints a home
  area over the scene so the dropped-unforbidden contract is testable.
