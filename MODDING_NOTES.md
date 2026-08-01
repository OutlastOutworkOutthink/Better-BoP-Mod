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
- Alpha 0.5.2 showed that neither model IDs nor the incoming label count are a
  reliable detection boundary: the mod loaded without error, but neither
  insertion path ran. Gate on `GameSettings.BaseGameMode == Custom`, then append
  to `GameSetupScreenView.listGameMode.data` after the vanilla view has rendered.
- Inputs may be localization keys, so never require the English text
  `Perfection`, `Domination`, or `Infinity` to identify the row.
- Alpha 0.5.3 logged that Creative was active while
  `GameSetupScreenView.listGameMode` was still unavailable during `OnShow`.
  Treat `OnShow` as an early diagnostic only. Recheck the legacy
  `GameSetupScreen.gameModeList` after `Show`, `OnScreenUpdated`, and
  `RefreshValuesFromSettings`, and recheck UI2 after its controller/view
  `RunLayout` callbacks.
- A visible Oblivion menu does not prove the game seed was activated. Arm from
  both setup-screen start callbacks as well as `CreateSinglePlayerGame`, keep
  the arm flag set until `GameState` exists, and persist at both `OnGameReady`
  and `OnLevelLoaded`.
- Oblivion relationships are invariants, not additive modifiers: return exactly
  `-200` for a bot observing the local human and exactly `+200` for a bot
  observing another bot. Apply this at last priority so difficulty and normal
  boons cannot override it.
- A null `PlayerState.AccountId` does not prove a player is a bot: offline local
  humans can also have no account ID. Check `GameManager.IsPlayerLocal(id)`
  first, then use `AutoPlay`/account state only for non-local players.
- The AI can consume the cached native `OpinionState.total`, not only the
  managed `GetOpinion` return. Enforce Oblivion totals after opinion refreshes
  and immediately before `AI.GetMove`; patch `AI.ShouldAcceptPeace` separately.
- Without Diplomacy, `PlayerInfoPopup.Refresh` does not call
  `GetLocalizedTopReasons`, so a label postfix alone cannot display reason
  pills. For Oblivion, hide only the locked-info panel and call the popup's
  native `SwapButtons` with the rebuilt three-reason dictionary. Do not fake
  tech ownership.
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
