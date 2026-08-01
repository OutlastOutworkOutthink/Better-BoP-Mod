# Better BoP modding notes

Compact implementation notes for repeatable fixes. Keep runtime claims tied to
an actual Polytopia/BepInEx log; a successful build proves compatibility with
the reference assemblies, not that the patched screen executed correctly.

## Creative game-mode UI (Polytopia 122 / PolyMod 1.2.17)

- Current PC Creative setup uses `GameSetupScreen_UI2` and renders its mode row
  through `GameSetupScreenView.SetShowGameModes` / `listGameMode`.
- Do not call `UIHorizontalListData.HasData()` from an `OnShow` postfix. On
  Polytopia 122 its native object may exist while internal fields are still
  null; Alpha 0.5.1 produced a `NullReferenceException` inside `HasData()` and
  aborted the Oblivion insertion.
- Check `labels` and `ids` individually before reading them. Wrap optional UI
  enhancement patches so a failed insertion cannot break the setup screen.
- At the final `SetShowGameModes` boundary, the normal Creative row contains
  exactly three entries. Use that structure instead of matching English text:
  inputs may be localization keys, so looking for `Perfection`, `Domination`,
  and `Infinity` can silently miss the correct row.
- If only the rendered list can be expanded, intercept selection of its fourth
  `Oblivion` entry before vanilla handles it. Vanilla's model may still contain
  only three items and would otherwise index past the end.

## Verification checklist

1. Build Release with zero warnings and errors.
2. Copy the Release DLL to the mod root and compare SHA-256 hashes.
3. Confirm archived features do not appear as symbols in the compiled DLL.
4. Package one `Better-BoP-Mod` folder with the DLL, manifest, patch, and README.
5. Test ZIP integrity and inspect the packaged manifest version.
6. In game, inspect the BepInEx log for both the Alpha load line and the
   `Added Oblivion ...` insertion line before treating the UI as verified.
