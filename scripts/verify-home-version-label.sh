#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"

if grep -R -E 'HarmonyPatch\(typeof\(StartScreen(_UI2)?\)' \
  --include='*.cs' "$root"; then
  echo "The version label must not patch the native StartScreen lifecycle." >&2
  exit 1
fi

grep -Fq 'DisplayText = "BBoP Alpha 0.6.13"' "$root/HomeVersionLabel.cs"
grep -Fq '[HarmonyPatch(typeof(UIEvents), nameof(UIEvents.ScreenOpen))]' "$root/HomeVersionLabel.cs"
grep -Fq 'openedScreen != UIConstants.Screens.StartScreen' "$root/HomeVersionLabel.cs"
grep -Fq '.GetScreen(openedScreen, true)?' "$root/HomeVersionLabel.cs"
grep -Fq '[HarmonyPatch(typeof(UIManager), nameof(UIManager.ShowScreen))]' "$root/HomeVersionLabel.cs"
grep -Fq 'SafePatch(typeof(HomeVersionShowScreenPatch), logger);' "$root/Main.cs"
grep -Fq 'TextAlignmentOptions.BottomRight' "$root/HomeVersionLabel.cs"
grep -Fq 'field.raycastTarget = false;' "$root/HomeVersionLabel.cs"
grep -Fq 'Better BoP Alpha 0.6.13 loaded' "$root/Main.cs"
grep -Fq '"version": "0.6.13"' "$root/manifest.json"

if grep -Fq '[HarmonyPatch(typeof(GameManager), "Update")]' "$root/HomeVersionLabel.cs"; then
  echo "The title label must not add per-frame work." >&2
  exit 1
fi

echo "Late, one-time home version label guards passed."
