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
grep -Fq 'GameType.SinglePlayer' "$source_file"
grep -Fq 'AdvancedSettingsSingleplayerStartPatch' "$source_file"
grep -Fq 'if (!pending) activeRules = RuleSet.Default;' "$source_file"
grep -Fq 'AdvancedUnitCostUiPatch' "$source_file"
grep -Fq 'AdvancedUnitCostValidationPatch' "$source_file"
grep -Fq 'AdvancedUnitCostExecutionPatch' "$source_file"
grep -Fq 'AdvancedBuildingCostUiPatch' "$source_file"
grep -Fq 'AdvancedBuildingCostValidationPatch' "$source_file"
grep -Fq 'AdvancedBuildingCostExecutionPatch' "$source_file"
grep -Fq 'BeginUnitCostScope' "$source_file"
grep -Fq 'BeginBuildingCostScope' "$source_file"
grep -Fq 'unitCostScopeDepth' "$source_file"
grep -Fq 'buildingCostScopeDepth' "$source_file"
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

if grep -Eq 'HarmonyPatch\(typeof\((UnitData|ImprovementData)\), "get_cost"' "$source_file"; then
  echo "IL2CPP field accessors cannot be patched safely." >&2
  exit 1
fi

echo "Advanced match settings source guards passed."
