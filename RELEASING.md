# Releasing

Manual local builds; the assembly is committed (`Assemblies/PawnsOptimizeWeaponQuality.dll`).
The build references only public NuGet packages (Krafs.Rimworld.Ref, Lib.Harmony) - no local
Steam or workshop DLLs - so it is CI-friendly. Combat Extended and Simple Sidearms are reached
by reflection at runtime; neither is referenced or vendored.

## Checklist

1. `dotnet build Source/PawnsOptimizeWeaponQuality/PawnsOptimizeWeaponQuality.csproj -c Release`
2. Automated pass, both profiles (see [TESTPLAN.md](TESTPLAN.md)) - every `test-results-*.json`
   must be `"passed": true`:
   - CE profile: `./test/run-lq-stage.sh`, then `./test/run-lq-assert.sh lqN <save>` for `lq1`..`lq5`
   - Vanilla profile: `./test/run-lqv-test.sh`
3. Manual UI check: the settings page shows the auto-upgrade toggle, the minimum-quality slider,
   and the hit-point tiebreak slider; settings persist across save/load.
4. Composition sanity: one load in a Combat Extended + Simple Sidearms profile (exercises the CE
   loadout path and the SS notify path); one load in vanilla (no CE) for the held-weapon path.
5. Demo GIF (owner): a pawn walks to the higher-quality copy, swaps, and drops the old one. Clip
   to `Media/` and embed in the README.
6. Tag `v1.0.0`; upload from the in-game Mods menu.

## Publishing (clean upload)

RimWorld's uploader ships the WHOLE mod folder (no `.rwignore`; `SetItemContent`
runs over the mod dir), so never upload the repo - it carries `Source/`, `test/`,
`docs/`, `Media/`, etc. Run `./publish.sh` to stage an allowlisted clean copy (About
+ Assemblies + Defs/Patches/Languages as applicable + LICENSE/NOTICE) into a sibling
`.publish/`, and upload that folder. After the first publish, copy the generated
`About/PublishedFileId.txt` back into the repo.

## Versioning & save compatibility

Semver. No per-save footprint - the mod scribes nothing (settings live in mod settings; the
per-pawn back-off cache is process-static). Safe to ADD or REMOVE mid-save.
