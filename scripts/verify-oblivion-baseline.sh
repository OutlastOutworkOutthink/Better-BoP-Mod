#!/bin/sh
set -eu

expected_mode="2f0af054422c6e95d63e86568a8187ee8f4366218e26fcf081e1c885a0be6290"
expected_opinions="3b3d086e6d35654f345f25cf1e879a139f4cf1cb8357df3a6e9bd1fdec08ed68"

actual_mode="$(shasum -a 256 OblivionMode.cs | cut -d ' ' -f 1)"
actual_opinions="$(shasum -a 256 OblivionOpinions.cs | cut -d ' ' -f 1)"

test "$actual_mode" = "$expected_mode" || {
  echo "OblivionMode.cs changed from the proven Alpha 0.5.6 baseline." >&2
  exit 1
}
test "$actual_opinions" = "$expected_opinions" || {
  echo "OblivionOpinions.cs changed from the proven Alpha 0.5.6 baseline." >&2
  exit 1
}

dll="bin/Release/net6.0/BetterBoPMod.dll"
test -f "$dll" || {
  echo "Build the Release DLL before running the full baseline check." >&2
  exit 1
}

for symbol in \
  OblivionCreativeModeListPatch \
  OblivionOpinionStoragePatch \
  OblivionPlayerOpinionValuePatch \
  OblivionAIMovePatch \
  OblivionAIPeacePatch \
  OblivionAIDiplomacyCommandPatch \
  DiscordAccountLink \
  DiscordProfileStartPatch \
  DiscordProfileEnablePatch \
  DiscordUILibraryReadyPatch \
  DiscordRoundButtonLayoutPatch \
  DiscordControllerClickPatch \
  UniversalPeaceRules \
  UniversalPeaceParsedRulesPatch \
  UniversalPeaceAbilityUnlockPatch \
  UniversalPeaceHasAbilityPatch \
  UniversalPeaceUnlockedAbilitiesPatch \
  UniversalPeaceRequiredTechPatch \
  UniversalPeaceTechTreePatch \
  UniversalPeaceTechPopupPatch \
  UniversalPeaceDiplomacyButtonPatch \
  UniversalPeaceUnavailablePopupPatch \
  UniversalPeaceAIPreparePatch
do
  strings "$dll" | grep -q "$symbol" || {
    echo "Release DLL is missing required symbol: $symbol" >&2
    exit 1
  }
done

for archived in GiftStars GrowGiant IntegratedMultiplayer TribeRelations BetterBoPRules
do
  if strings "$dll" | grep -q "$archived"; then
    echo "Release DLL unexpectedly contains archived feature: $archived" >&2
    exit 1
  fi
done

for rejected in DiscordPointerClickPatch OnApplicationFocusChanged RefreshAfterFocusSettlesAsync
do
  if strings "$dll" | grep -q "$rejected"; then
    echo "Release DLL contains rejected compatibility path: $rejected" >&2
    exit 1
  fi
done

echo "Locked Oblivion and Discord baselines plus isolated universal peace verified."
