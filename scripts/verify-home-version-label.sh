#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"

if grep -R -E 'HomeVersion|HarmonyPatch\(typeof\(StartScreen(_UI2)?\)' \
  --include='*.cs' "$root"; then
  echo "Alpha 0.6.8 must not patch or mutate the native home-screen lifecycle." >&2
  exit 1
fi

grep -Fq 'Better BoP Alpha 0.6.8 loaded' "$root/Main.cs"
grep -Fq '"version": "0.6.8"' "$root/manifest.json"

echo "Home-screen lifecycle isolation guards passed."
