#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
source_file="$root/HomeVersionLabel.cs"

grep -Fq 'BBoP Alpha 0.6.1' "$source_file"
grep -Fq 'new Vector2(1f, 0f)' "$source_file"
grep -Fq 'TextAlignmentOptions.BottomRight' "$source_file"
grep -Fq 'HomeVersionInitPatch' "$source_file"
grep -Fq 'HomeVersionShowPatch' "$source_file"
grep -Fq 'Il2CppType.Of<RectTransform>()' "$source_file"
grep -Fq 'isolated bottom-right text component' "$source_file"

if grep -Eq 'GetComponentsInChildren|Object\.Instantiate|HomeVersion(AfterLayout|OnShow|Layout|Refresh)Patch|\[HarmonyPatch\(typeof\(StartScreen_UI2\), "(Update|RunLayout|RefreshVersionInfo|OnShow)"\)\]' "$source_file"; then
  echo "Home version label must not scan, clone, poll, or re-enter stock layout." >&2
  exit 1
fi

echo "Home-screen version label source guards passed."
