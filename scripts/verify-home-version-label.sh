#!/usr/bin/env bash
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"

if grep -R -E 'HarmonyPatch\(typeof\(StartScreen(_UI2)?\)' \
  --include='*.cs' "$root"; then
  echo "The version label must not patch the native StartScreen lifecycle." >&2
  exit 1
fi

grep -Fq 'DisplayText = "BBoP Alpha 0.6.14"' "$root/HomeVersionLabel.cs"
grep -Fq '[HarmonyPatch(typeof(UIEvents), nameof(UIEvents.ScreenOpen))]' "$root/HomeVersionLabel.cs"
grep -Fq '[HarmonyPatch(typeof(UIEvents), nameof(UIEvents.LoadingScreenHidden))]' "$root/HomeVersionLabel.cs"
grep -Fq 'new[] { typeof(UIConstants.Screens), typeof(bool), typeof(UIDeepLinkData) })]' "$root/HomeVersionLabel.cs"
grep -Fq 'IScreen __result' "$root/HomeVersionLabel.cs"
grep -Fq 'UIManager.Instance.GetCurrentScreen()' "$root/HomeVersionLabel.cs"
grep -Fq 'private const int MaxAttempts = 240;' "$root/HomeVersionLabel.cs"
grep -Fq 'HomeVersionLabel.Tick();' "$root/IntegratedModdedGames.cs"
grep -Fq 'SafePatch(typeof(HomeVersionLoadingCompletePatch), logger);' "$root/Main.cs"
grep -Fq 'SafePatch(typeof(HomeVersionShowScreenPatch), logger);' "$root/Main.cs"
grep -Fq 'localizer.enabled = false;' "$root/HomeVersionLabel.cs"
grep -Fq 'textField?.SetText(DisplayText);' "$root/HomeVersionLabel.cs"
grep -Fq 'TextAlignmentOptions.BottomRight' "$root/HomeVersionLabel.cs"
grep -Fq 'GetComponentInParent<Canvas>()' "$root/HomeVersionLabel.cs"
grep -Fq '.rootCanvas?.GetComponent<RectTransform>()' "$root/HomeVersionLabel.cs"
grep -Fq 'UnityEngine.Object.Instantiate(template.gameObject, parent)' "$root/HomeVersionLabel.cs"
grep -Fq 'transform.anchorMin = new Vector2(1f, 0f);' "$root/HomeVersionLabel.cs"
grep -Fq 'transform.anchorMax = new Vector2(1f, 0f);' "$root/HomeVersionLabel.cs"
grep -Fq 'transform.pivot = new Vector2(1f, 0f);' "$root/HomeVersionLabel.cs"
grep -Fq 'transform.anchoredPosition = new Vector2(-24f, 20f);' "$root/HomeVersionLabel.cs"
grep -Fq 'label.gameObject.SetActive(false);' "$root/HomeVersionLabel.cs"
grep -Fq 'field.raycastTarget = false;' "$root/HomeVersionLabel.cs"
grep -Fq 'Better BoP Alpha 0.6.14 loaded' "$root/Main.cs"
grep -Fq '"version": "0.6.14"' "$root/manifest.json"

if grep -Fq '[HarmonyPatch(typeof(GameManager), "Update")]' "$root/HomeVersionLabel.cs"; then
  echo "The title label must not add per-frame work." >&2
  exit 1
fi

if grep -Fq 'Loaded Better BoP patch:' "$root/Main.cs"; then
  echo "Successful patch registration must remain silent." >&2
  exit 1
fi

echo "Deferred, bounded home version label guards passed."
