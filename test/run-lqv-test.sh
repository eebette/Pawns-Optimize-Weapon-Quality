#!/usr/bin/env bash
# Single-phase VANILLA-profile test (Core + Harmony + LoadoutQuality, NO Combat Extended).
# Arranges + asserts in one run; writes test/SaveDataVanilla/test-results-lqv.json.
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
RIMWORLD="$HOME/.local/share/Steam/steamapps/common/RimWorld/RimWorldLinux"
# GS_WRAP: launch inside gamescope's nested compositor - immune to the desktop's
# display state (owner gaming via Proton, mode-list churn, XF86VidMode crashes).
GS=(gamescope -W 1600 -H 900 --)
SAVEDATA="$REPO/test/SaveDataVanilla"
mkdir -p "$SAVEDATA/Config"
# Overwrite each run: this profile is the whole point, and there are no saves to preserve.
cp "$REPO/test/Config/ModsConfig.vanilla.xml" "$SAVEDATA/Config/ModsConfig.xml"
cp "$REPO/test/Config/Prefs.xml" "$SAVEDATA/Config/Prefs.xml"
RESULT="$SAVEDATA/test-results-lqv.json"
if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
    dotnet build "$REPO/Source/PawnsOptimizeWeaponQuality/PawnsOptimizeWeaponQuality.csproj" -c Release
    dotnet build "$REPO/test/StagingModVanilla/Source/LQTestStagingVanilla.csproj" -c Release
fi
rm -f "$RESULT"
timeout --signal=TERM 20m "${GS[@]}" "$RIMWORLD" -savedatafolder="$SAVEDATA" -quicktest -lqvtest || true
if [[ -f "$RESULT" ]]; then
    echo "== results =="; cat "$RESULT"
else
    echo "NO RESULTS FILE" >&2; exit 1
fi
