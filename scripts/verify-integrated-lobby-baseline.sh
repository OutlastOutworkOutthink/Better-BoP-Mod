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
require_text '"waiting_for_tribes" when !ownTribe.HasValue => "CHOOSE TRIBE"' "$source_file"
require_text '"ready_to_start" when match.Role == "host"' "$source_file"
require_text 'button.BadgeEnabled = false;' "$source_file"
require_text 'LoadTribelessHeadMethod?.Invoke' "$source_file"
require_text 'Your tribe is already locked for this game.' "$source_file"
require_text 'StartMatchAsync(match.Id)' "$source_file"
require_text 'IntegratedLobbyButtonStatePatch' "$main_file"
require_text 'IntegratedLobbyDescriptionPatch' "$main_file"
require_text 'popup.Description = GetIntegratedLobbyDescription' "$source_file"
require_text 'if (!opponentTribe.HasValue) return "Waiting for your opponent to choose a tribe.";' "$source_file"

reject_text 'Change Your Tribe' "$source_file"
reject_text 'Refresh Modded Games' "$source_file"

echo "Vanilla-style Integrated lobby baseline verified."
