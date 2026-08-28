#!/usr/bin/env bash
set -euo pipefail

source_file="IntegratedModdedGames.cs"
main_file="Main.cs"

require_text() {
  local text="$1"
  local file="$2"
  if ! grep -Fq "$text" "$file"; then
    echo "Missing Integrated lobby baseline: $text ($file)" >&2
    exit 1
  fi
}

reject_text() {
  local text="$1"
  local file="$2"
  if grep -Fq "$text" "$file"; then
    echo "Obsolete Integrated lobby UI returned: $text ($file)" >&2
    exit 1
  fi
}

require_text 'screen.AddLobbyRow(BuildLobbyViewModel(match));' "$source_file"
require_text 'private const int IntegratedTurnTimeMinutes = 24 * 60;' "$source_file"
require_text 'private const int TinyDrylandSideLength = 11;' "$source_file"
require_text 'TimeLimit = IntegratedTurnTimeMinutes,' "$source_file"
require_text 'MapSize = TinyDrylandSideLength,' "$source_file"
require_text 'private static bool CanPickOwnTribe(IntegratedMatch match, int? ownTribe)' "$source_file"
require_text '!ownTribe.HasValue && match.Status is ("waiting_for_tribes" or "ready_to_start")' "$source_file"
require_text 'string? label = CanPickOwnTribe(match, ownTribe) ? "PICK TRIBE"' "$source_file"
require_text 'if (action == "start" && CanPickOwnTribe(match, ownTribe))' "$source_file"
require_text 'CanPickOwnTribe(match, selectedTribe)' "$source_file"
require_text '"ready_to_start" when match.Role == "host"' "$source_file"
require_text 'button.BadgeEnabled = false;' "$source_file"
require_text 'HideIntegratedReadyBadges(popup);' "$source_file"
require_text 'Your tribe is already locked for this game.' "$source_file"
require_text 'StartMatchAsync(match.Id)' "$source_file"
require_text 'pendingTribePickerMatchId = match.Id;' "$source_file"
require_text 'GameManager.PreliminaryGameSettings = BuildPickerSettings(match);' "$source_file"
require_text 'UIManager.Instance.ShowScreen(UIConstants.Screens.TribePicker, false, null);' "$source_file"
require_text 'internal static bool SubmitIntegratedTribe(TribePickerScreen_UI2 picker)' "$source_file"
require_text 'picker.selectedTribe?.type ?? TribeType.None' "$source_file"
require_text '_ = SelectTribeAsync(matchId, tribe);' "$source_file"
require_text 'MutateMatchAsync(matchId, "tribe", new { tribe })' "$source_file"
require_text '$"/v1/integrated-matches/{matchId}/{action}"' "$source_file"
require_text 'UIManager.Instance.PopCurrentScreen();' "$source_file"
require_text 'IntegratedTribePickerSubmitPatch' "$main_file"
require_text 'SafePatch(typeof(IntegratedTribePickerSubmitPatch), logger);' "$main_file"
require_text 'IntegratedTribePickerClosePatch' "$main_file"
require_text 'SafePatch(typeof(IntegratedTribePickerClosePatch), logger);' "$main_file"
require_text 'IntegratedSyntheticPlayerDataPatch' "$main_file"
require_text 'TryGetIntegratedPlayerData' "$source_file"
require_text 'ParticipatorViewModel __0' "$source_file"
require_text 'IntegratedLobbyButtonStatePatch' "$main_file"
require_text 'IntegratedLobbyDescriptionPatch' "$main_file"
require_text 'SafePatch(typeof(ModdedPullRefreshPatch), logger);' "$main_file"
require_text 'popup.Description = GetIntegratedLobbyDescription' "$source_file"
require_text 'if (!opponentTribe.HasValue) return "Waiting for your opponent to choose a tribe.";' "$source_file"
require_text 'internal static bool HandlePullRefresh(MultiplayerScreen screen)' "$source_file"
require_text 'await RefreshMatchesAsync(true, true).ConfigureAwait(false);' "$source_file"
require_text 'owner?.multiplayerScreen?.refresher?.EndRefreshing();' "$source_file"
require_text '[HarmonyPatch(typeof(MultiplayerScreen), "OnRefreshGames")]' "$source_file"

reject_text 'Change Your Tribe' "$source_file"
reject_text 'Refresh Modded Games' "$source_file"
reject_text 'NativeMapSideLength' "$source_file"
reject_text "Polytopia's tribe picker is not available." "$source_file"
reject_text 'TribeSelectorScreen' "$source_file"
reject_text 'IntegratedTribePickerLifecyclePatch' "$source_file"
reject_text 'ConfigureTribePicker' "$source_file"
reject_text 'LoadFaceIcon' "$source_file"
reject_text '!ownTribe.HasValue && match.Status == "waiting_for_tribes"' "$source_file"

open_picker_body="$(sed -n '/private static void OpenTribePicker/,/^    }/p' "$source_file")"
if grep -Eq 'GetScreen\(|ConfigureTribePicker\(' <<<"$open_picker_body"; then
  echo "Integrated tribe picker must bind through the vanilla screen lifecycle." >&2
  exit 1
fi

echo "Vanilla-style Integrated lobby baseline verified."
