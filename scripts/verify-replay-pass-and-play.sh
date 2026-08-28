#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
source_file="$root/ReplayPassAndPlay.cs"
main_file="$root/Main.cs"

grep -Fq 'command is MoveCommand or ResearchCommand' "$source_file"
grep -Fq 'ClientBase.CreateFakeSession(stateClone.GameState, player)' "$source_file"
grep -Fq 'branch.clientType = ClientBase.ClientType.PassAndPlay;' "$source_file"
grep -Fq 'branch.doAutoSwitchPlayers = true;' "$source_file"
grep -Fq 'branch.SetNewLocalPlayerTurnForPassAndPlay(player);' "$source_file"
grep -Fq 'GameManager.Instance.fakeSession = branch;' "$source_file"
grep -Fq 'GameManager.Client?.clientType != ClientBase.ClientType.PassAndPlay' "$source_file"
grep -Fq 'CaptureSnapshot(client)' "$source_file"
grep -Fq 'DecodeSnapshot(snapshot) as ReplayClient' "$source_file"
grep -Fq 'GameManager.Instance.SetReplayClient(replay);' "$source_file"
grep -Fq 'ClientInteraction.SelectTileInternal' "$source_file"
grep -Fq 'ReplayPassAndPlayMoveIntentPatch' "$source_file"
grep -Fq 'ReplayPassAndPlayTechIntentPatch' "$source_file"
grep -Fq 'ReplayPassAndPlayResearchFallbackPatch' "$source_file"
grep -Fq 'UnitState replayUnit = unit.UnitState;' "$source_file"
grep -Fq 'MoveFromReplay(replayUnit, tile.Coordinates)' "$source_file"
grep -Fq 'state.TryGetUnit(replayUnit.id, out UnitState branchUnit)' "$source_file"
grep -Fq 'MoveCommand command = new(state.CurrentPlayer, branchUnit, destination);' "$source_file"
grep -Fq 'state.TryGetUnit(move.UnitId, out UnitState unit)' "$source_file"
grep -Fq 'mapped = new MoveCommand(state.CurrentPlayer, unit, move.To);' "$source_file"
grep -Fq 'mapped = new ResearchCommand(state.CurrentPlayer, research.Type);' "$source_file"
grep -Fq 'TryBeginFromInput("tech-click")' "$source_file"
grep -Fq 'ResearchCommand intent = new(state.CurrentPlayer, __instance.TechData.type);' "$source_file"
grep -Fq 'intent.IsValid(state)' "$source_file"
grep -Fq '__instance.RefreshState(false);' "$source_file"
grep -Fq 'Replay snapshot did not preserve the selected position.' "$source_file"
grep -Fq 'timeline.gameObject.SetActive(false);' "$source_file"
grep -Fq '.SetText("Back")' "$source_file"
grep -Fq 'hud?.buttonBar?.RefreshNextTurnButton();' "$source_file"
grep -Fq 'ReplayPassAndPlayNoSavePatch' "$source_file"
grep -Fq 'GameState gameState' "$source_file"

for patch in \
  ReplayPassAndPlayMoveIntentPatch \
  ReplayPassAndPlayTechStatePatch \
  ReplayPassAndPlayTechIntentPatch \
  ReplayPassAndPlayResearchFallbackPatch \
  ReplayPassAndPlayInputPatch \
  ReplayPassAndPlayCommandPatch \
  ReplayPassAndPlayHudPatch \
  ReplayPassAndPlayUiPatch \
  ReplayPassAndPlayReadyPatch \
  ReplayPassAndPlayNoSavePatch
do
  grep -Fq "SafePatch(typeof($patch), logger);" "$main_file"
done

if grep -Fq 'ReplayPassAndPlayNoNetworkPatch' "$source_file"; then
  echo "Replay branches must use Polytopia's local pass-and-play command routing." >&2
  exit 1
fi

if grep -Fq 'GameManager.Instance.client = branch;' "$source_file"; then
  echo "Replay branches must use Polytopia's temporary local-session slot." >&2
  exit 1
fi

if grep -Eq 'HarmonyPatch\(typeof\([^)]*\), "?(Update|LateUpdate|FixedUpdate)"?\)' "$source_file"; then
  echo "Replay branches must not poll per frame." >&2
  exit 1
fi

snapshot_count="$(grep -Fc 'CaptureSnapshot(client)' "$source_file")"
test "$snapshot_count" -eq 1

echo "Replay pass-and-play transaction guards passed."
