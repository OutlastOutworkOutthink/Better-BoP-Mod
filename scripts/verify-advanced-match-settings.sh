#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
source_file="$root/AdvancedMatchSettings.cs"

grep -Fq '"Show Advanced Settings"' "$source_file"
grep -Fq '"Hide Advanced Settings"' "$source_file"
grep -Fq '"Unit cost multiplayer"' "$source_file"
grep -Fq '"Building cost multiplier"' "$source_file"
grep -Fq '"Enemy unit health"' "$source_file"
grep -Fq '25, 50, 100, 150, 200, 300, 500' "$source_file"
grep -Fq 'AdvancedSettingsOnShowPatch' "$source_file"
grep -Fq 'AdvancedSettingsLayoutPatch' "$source_file"
grep -Fq 'AdvancedSettingsTogglePatch' "$source_file"
grep -Fq 'typeof(UnitData), "get_cost"' "$source_file"
grep -Fq 'typeof(ImprovementData), "get_cost"' "$source_file"
grep -Fq 'nameof(UnitDataExtensions.GetMaxHealth)' "$source_file"
grep -Fq 'AdvancedConvertedUnitHealthPatch' "$source_file"
grep -Fq 'GameRulesKeyPrefix' "$source_file"

test $(( (2 * 500 + 99) / 100 )) -eq 10
test $(( (8 * 500 + 99) / 100 )) -eq 40
test $(( (100 * 500 + 99) / 100 )) -eq 500
test $(( (3 * 50 + 99) / 100 )) -eq 2

if grep -Fq '[HarmonyPatch(typeof(GameManager), "Update")]' "$source_file"; then
  echo "Advanced settings must not add per-frame GameManager work." >&2
  exit 1
fi

echo "Advanced match settings source guards passed."
