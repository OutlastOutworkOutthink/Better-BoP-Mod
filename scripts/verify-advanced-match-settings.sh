#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
source_file="$root/AdvancedMatchSettings.cs"

grep -Fq '"Show Advanced Settings"' "$source_file"
grep -Fq '"Hide Advanced Settings"' "$source_file"
grep -Fq '"Unit cost for you"' "$source_file"
grep -Fq '"Your building cost"' "$source_file"
grep -Fq '"Enemy unit health"' "$source_file"
grep -Fq '25, 50, 100, 150, 200, 300, 500' "$source_file"
grep -Fq 'AdvancedSettingsOnShowPatch' "$source_file"
grep -Fq 'AdvancedSettingsLayoutPatch' "$source_file"
grep -Fq 'AdvancedSettingsViewLayoutPatch' "$source_file"
grep -Fq 'ToggleName = "BetterBoP.AdvancedSettingsToggle"' "$source_file"
grep -Fq 'UILibrary.NewLabelButton(holder)' "$source_file"
grep -Fq 'BindToggle(screen, controls)' "$source_file"
grep -Fq 'controls.Toggle.ClearCallbacks();' "$source_file"
grep -Fq 'controls.Toggle.OnClickedSignal.Add(controls.ToggleAction);' "$source_file"
grep -Fq 'controls.ToggleAction = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>' "$source_file"
grep -Fq 'private static void ToggleAdvanced(GameSetupScreen_UI2 screen)' "$source_file"
grep -Fq 'UILibrary.NewHorizontalList(holder)' "$source_file"
grep -Fq 'UILibrary.NewText(holder, text)' "$source_file"
grep -Fq 'ControlsByHolder' "$source_file"
grep -Fq 'FindExistingControls' "$source_file"
grep -Fq 'DiscardPartialOrDuplicateControls' "$source_file"
grep -Fq 'PruneOtherControlHolders' "$source_file"
grep -Fq 'Components.All(component =>' "$source_file"
grep -Fq 'NormalizeComponentOrder' "$source_file"
grep -Fq 'view.whatToShow |= GameSetupScreenView.Show.MapTypeList' "$source_file"
grep -Fq 'view.whatToShow |= GameSetupScreenView.Show.MapSizeList' "$source_file"
grep -Fq 'view.whatToShow &= ~GameSetupScreenView.Show.MapTypeList' "$source_file"
grep -Fq 'view.whatToShow &= ~GameSetupScreenView.Show.MapSizeList' "$source_file"
grep -Fq 'view.whatToShow &= ~GameSetupScreenView.Show.AdvancedSettingsToggle' "$source_file"
grep -Fq 'FindComponentIndex(view, view.continueButton)' "$source_file"
grep -Fq 'NormalizeSiblingOrder' "$source_file"
grep -Fq 'transform.SetSiblingIndex(view.holder.childCount - 1)' "$source_file"
grep -Fq 'transform.SetSiblingIndex(continueTransform.GetSiblingIndex())' "$source_file"
grep -Fq 'PrepareViewLayout' "$source_file"
grep -Fq 'FinalizeViewLayout' "$source_file"
grep -Fq 'AdvancedSettingsScrollerLayoutPatch' "$source_file"
grep -Fq 'CaptureNativeScrollerHeight' "$source_file"
grep -Fq '[HarmonyPriority(Priority.Last)]' "$source_file"
grep -Fq 'float oldContinueTop = view.continueButton.GetTop();' "$source_file"
grep -Fq 'controls.Toggle.SetPositionTopY(controls.Toggle.GetX(), cursorTop)' "$source_file"
grep -Fq 'row.SetPositionTopY(row.GetX(), cursorTop)' "$source_file"
grep -Fq 'view.continueButton.SetPositionTopY(view.continueButton.GetX(), cursorTop)' "$source_file"
grep -Fq 'nativeContentHeight + addedHeight' "$source_file"
grep -Fq 'scroller.UpdateContentBounds();' "$source_file"
grep -Fq 'screen.advancedSettingsExpanded = controls.Expanded;' "$source_file"
grep -Fq 'screen.UpdateLayout();' "$source_file"
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

if grep -Fq 'screen.view.RunLayout' "$source_file"; then
  echo "Advanced settings must relayout through ScreenBase_UI2.UpdateLayout, not recurse into the view." >&2
  exit 1
fi

if grep -Fq 'UnityEngine.Object.Instantiate(template.gameObject' "$source_file"; then
  echo "Advanced rows must be clean native controls, not clones of a populated setup list." >&2
  exit 1
fi

if grep -Fq 'RefreshVisibilityAfterLayout' "$source_file"; then
  echo "Advanced rows must be made visible before native view layout, not afterward." >&2
  exit 1
fi

if grep -Fq 'AdvancedSettingsTogglePatch' "$source_file"; then
  echo "Advanced settings must use the directly wired Better BoP toggle, not the variant-dependent native callback." >&2
  exit 1
fi

if grep -Fq 'SetShowAdvancedSettingsToggleButton' "$source_file"; then
  echo "Advanced settings must not reactivate Polytopia's fixed pre-map toggle." >&2
  exit 1
fi

echo "Advanced match settings source guards passed."
