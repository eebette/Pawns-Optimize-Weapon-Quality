#!/usr/bin/env bash
# Usage: ./test/run-lq-assert.sh lq1 LQ-1-ranged
set -euo pipefail
SCENARIO="${1:?scenario (lq1|lq2|lq3|lq4)}"
SAVE="${2:?save name}"
REPO="$(cd "$(dirname "$0")/.." && pwd)"
RIMWORLD="$HOME/.local/share/Steam/steamapps/common/RimWorld/RimWorldLinux"
# GS_WRAP: launch inside gamescope's nested compositor — immune to the desktop's
# display state (owner gaming via Proton, mode-list churn, XF86VidMode crashes).
GS=(gamescope -W 1600 -H 900 --)
SAVEDATA="$REPO/test/SaveData"
mkdir -p "$SAVEDATA/Config" "$SAVEDATA/Saves"
for f in ModsConfig.xml Prefs.xml; do
    [[ -e "$SAVEDATA/Config/$f" ]] || cp "$REPO/test/Config/$f" "$SAVEDATA/Config/$f"
done
RESULT="$SAVEDATA/test-results-$SCENARIO.json"
if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
    dotnet build "$REPO/Source/LoadoutQuality/LoadoutQuality.csproj" -c Release
    dotnet build "$REPO/test/StagingMod/Source/LQTestStaging.csproj" -c Release
fi
rm -f "$RESULT"
timeout --signal=TERM 20m "${GS[@]}" "$RIMWORLD" -savedatafolder="$SAVEDATA" \
    "-celoadsave=$SAVE" "-ceassert=$SCENARIO" || true
if [[ -f "$RESULT" ]]; then
    echo "== results =="; cat "$RESULT"
else
    echo "NO RESULTS FILE" >&2; exit 1
fi
