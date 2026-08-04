#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
source_file="$root/HomeVersionLabel.cs"

grep -Fq 'BBoP Alpha 0.5.25' "$source_file"
grep -Fq 'new Vector2(1f, 0f)' "$source_file"
grep -Fq 'TextAlignmentOptions.BottomRight' "$source_file"
grep -Fq 'HomeVersionAfterLayoutPatch' "$source_file"
grep -Fq 'ScreensBeingBound' "$source_file"
grep -Fq 'if (!string.Equals(field.text, desired' "$source_file"

if grep -Eq 'HomeVersion(OnShow|Layout|Refresh)Patch|\[HarmonyPatch\(typeof\(StartScreen_UI2\), "(Update|RunLayout|RefreshVersionInfo|OnShow)"\)\]' "$source_file"; then
  echo "Home version label must run only after the native layout completes." >&2
  exit 1
fi

echo "Home-screen version label source guards passed."
