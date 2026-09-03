#!/usr/bin/env bash
# Build + regenerate QUAL-* saves in the CE+SS suite test profile.
set -euo pipefail
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
if [[ "${SKIP_BUILD:-0}" != "1" ]]; then
    dotnet build "$REPO/Source/LoadoutQuality/LoadoutQuality.csproj" -c Release
    dotnet build "$REPO/test/StagingMod/Source/LQTestStaging.csproj" -c Release
fi
rm -f "$SAVEDATA/Saves"/QUAL-*.rws "$SAVEDATA/Saves"/LQ-*.rws
exec "${GS[@]}" "$RIMWORLD" -savedatafolder="$SAVEDATA" -quicktest -lqstage
