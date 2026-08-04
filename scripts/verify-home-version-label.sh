#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
source_file="$root/HomeVersionLabel.cs"

grep -Fq 'BBoP Alpha 0.6.2' "$source_file"
grep -Fq 'HomeVersionShowPatch' "$source_file"
grep -Fq 'aboutButton?.titleTextField?.textField' "$source_file"
grep -Fq 'native About label' "$source_file"

if grep -Eq 'GetComponentsInChildren|Object\.Instantiate|new GameObject|AddComponent|Il2CppReferenceArray|HomeVersionInitPatch|HomeVersion(AfterLayout|OnShow|Layout|Refresh)Patch|\[HarmonyPatch\(typeof\(StartScreen_UI2\), "(Update|RunLayout|RefreshVersionInfo|OnShow)"\)\]' "$source_file"; then
  echo "Home version text must reuse native UI without scanning, cloning, creating, polling, or re-entering stock layout." >&2
  exit 1
fi

echo "Home-screen version label source guards passed."
