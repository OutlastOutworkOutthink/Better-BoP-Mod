#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
source_file="$root/ReplayPassAndPlay.cs"

grep -Fq 'command is MoveCommand or ResearchCommand' "$source_file"
grep -Fq 'client.clientType = ClientBase.ClientType.PassAndPlay;' "$source_file"
grep -Fq 'client.doAutoSwitchPlayers = true;' "$source_file"
grep -Fq 'client.SetNewLocalPlayerTurnForPassAndPlay(client.GameState.CurrentPlayer);' "$source_file"
grep -Fq 'restoreClient = CloneReplay(client);' "$source_file"
grep -Fq 'GameManager.Instance.SetReplayClient(replay);' "$source_file"
grep -Fq 'timeline.gameObject.SetActive(false);' "$source_file"
grep -Fq '.SetText("Back")' "$source_file"
grep -Fq 'hud?.buttonBar?.RefreshNextTurnButton();' "$source_file"
grep -Fq 'ReplayPassAndPlayNoSavePatch' "$source_file"
grep -Fq 'ReplayPassAndPlayNoNetworkPatch' "$source_file"

if grep -Eq 'HarmonyPatch\(typeof\([^)]*\), "?(Update|LateUpdate|FixedUpdate)"?\)' "$source_file"; then
  echo "Replay branches must not poll per frame." >&2
  exit 1
fi

clone_count="$(grep -Fc 'CloneReplay(client)' "$source_file")"
test "$clone_count" -eq 1

echo "Replay pass-and-play transaction guards passed."
