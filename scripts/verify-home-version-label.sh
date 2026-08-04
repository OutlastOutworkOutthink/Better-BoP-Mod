#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
source_file="$root/HomeVersionLabel.cs"

grep -Fq 'BBoP Alpha 0.5.24' "$source_file"
grep -Fq 'new Vector2(1f, 0f)' "$source_file"
grep -Fq 'TextAlignmentOptions.BottomRight' "$source_file"
grep -Fq 'HomeVersionOnShowPatch' "$source_file"
grep -Fq 'HomeVersionAfterLayoutPatch' "$source_file"
grep -Fq 'HomeVersionLayoutPatch' "$source_file"
grep -Fq 'HomeVersionRefreshPatch' "$source_file"

if grep -Fq '[HarmonyPatch(typeof(StartScreen_UI2), "Update")]' "$source_file"; then
  echo "Home version label must not add per-frame work." >&2
  exit 1
fi

echo "Home-screen version label source guards passed."
