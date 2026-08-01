using HarmonyLib;
using I2.Loc;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BetterBoPMod;

internal static class OblivionOpinions
{
    internal const string EnemyLabel = "the enemy";
    internal const float BotAllianceBonus = 200f;
    internal const float PlayerEnemyPenalty = -200f;

    internal static bool IsBot(PlayerState player)
    {
        if (player == null || player.Id == PlayerState.NATURE_PLAYER_ID) return false;

        // Offline/local humans can also have no AccountId. Local identity must
        // win over the account heuristic or the human is mistaken for a bot
        // and receives the bot-alliance +200 opinion.
        if (IsLocal(player.Id)) return false;
        return player.AutoPlay || !player.AccountId.HasValue;
    }

    internal static bool IsLocal(byte playerId)
    {
        try
        {
            return GameManager.IsPlayerLocal(playerId);
        }
        catch
        {
            return GameManager.LocalPlayer?.Id == playerId;
        }
    }

    internal static bool ShouldShowEnemyReason(PlayerState observer, PlayerState subject, GameState state)
    {
        return OblivionMode.IsActive(state) && IsBot(observer) && subject != null && IsLocal(subject.Id);
    }

    internal static bool TryGetForcedOpinion(
        GameState state,
        PlayerState observer,
        PlayerState subject,
        out float value
    )
    {
        value = 0f;
        if (!OblivionMode.IsActive(state) || !IsBot(observer) || subject == null) return false;

        // Check local identity first. An offline local player may have the same
        // null AccountId shape as a bot.
        if (IsLocal(subject.Id))
        {
            value = PlayerEnemyPenalty;
            return true;
        }

        if (!IsBot(subject)) return false;
        value = BotAllianceBonus;
        return true;
    }

    /// <summary>
    /// Writes the Oblivion values into the opinion state read by native AI.
    /// Harmony return-value patches cover managed callers, but the IL2CPP AI
    /// can read the cached OpinionState directly while choosing its moves.
    /// </summary>
    internal static void EnforceStoredOpinions(GameState state, PlayerState observer)
    {
        if (!OblivionMode.IsActive(state) || !IsBot(observer) || observer.opinions == null) return;

        Il2CppSystem.Collections.Generic.Dictionary<byte, OpinionState> stored =
            observer.opinions.Opinions;
        if (stored == null)
        {
            stored = new Il2CppSystem.Collections.Generic.Dictionary<byte, OpinionState>();
            observer.opinions.Opinions = stored;
        }

        foreach (PlayerState subject in state.PlayerStates)
        {
            if (subject == null || subject.Id == observer.Id ||
                !TryGetForcedOpinion(state, observer, subject, out float forced))
                continue;

            if (!stored.TryGetValue(subject.Id, out OpinionState opinion) || opinion == null)
            {
                opinion = new OpinionState();
                stored[subject.Id] = opinion;
            }

            // Preserve native reason values for the two standard UI pills, but
            // make the real total used by AI an exact Oblivion invariant.
            opinion.total = forced;
        }
    }

    internal static string NativeLabel(OpinionManager.Type type) => type switch
    {
        OpinionManager.Type.CommonRelation => ScriptLocalization.opinion_reason_commonrelation,
        OpinionManager.Type.DifferentRelation => ScriptLocalization.opinion_reason_differentrelation,
        OpinionManager.Type.Peaceful => ScriptLocalization.opinion_reason_peaceful,
        OpinionManager.Type.Aggression => ScriptLocalization.opinion_reason_aggression,
        OpinionManager.Type.Embassy => ScriptLocalization.opinion_reason_embassy,
        OpinionManager.Type.Threatening => ScriptLocalization.opinion_reason_threatening,
        OpinionManager.Type.Intrusive => ScriptLocalization.opinion_reason_intrusive,
        OpinionManager.Type.Winning => ScriptLocalization.opinion_reason_winning,
        OpinionManager.Type.Brave => ScriptLocalization.opinion_reason_brave,
        OpinionManager.Type.Dislike => ScriptLocalization.opinion_reason_dislike,
        OpinionManager.Type.Like => ScriptLocalization.opinion_reason_like,
        OpinionManager.Type.Weak => ScriptLocalization.opinion_reason_weak,
        OpinionManager.Type.Powerful => ScriptLocalization.opinion_reason_powerful,
        _ => string.Empty,
    };

    internal static void ShowEnemyDescription()
    {
        BasicPopup popup = PopupManager.GetBasicPopup()
            .SetHeader("The Enemy")
            .SetDescription("You are the enemy.");
        Il2CppReferenceArray<PopupBase.PopupButtonData> buttons = new(1);
        buttons[0] = new PopupBase.PopupButtonData
        {
            id = 0,
            text = "Okay",
            state = PopupBase.PopupButtonData.States.None,
            closesPopup = true,
            callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => { }),
        };
        popup.SetButtonData(buttons, false);
        popup.RunLayout();
        popup.Show();
    }

    internal static void ShowEnemyInsight(PlayerInfoPopup popup)
    {
        if (popup == null) return;
        PlayerState observer = popup.player;
        PlayerState subject = GameManager.LocalPlayer;
        GameState state = GameManager.GameState;
        if (observer == null || subject == null || state == null ||
            !ShouldShowEnemyReason(observer, subject, state)) return;

        try
        {
            EnforceStoredOpinions(state, observer);
            var reasons = observer.GetReasons(state, subject.Id);
            string text = popup.GetLocalizedTopReasons(reasons, out var colors);

            // Oblivion's defining rule is not hidden behind Diplomacy research.
            // Show the ordinary three-pill layout without unlocking any tech or
            // changing which diplomacy commands the player can use.
            if (popup.lockedInfoContainer != null)
                popup.lockedInfoContainer.gameObject.SetActive(false);
            if (popup.opinionText != null)
            {
                popup.opinionText.gameObject.SetActive(true);
                popup.opinionText.text = text;
            }
            popup.SwapButtons(Color.red, ScriptTerms.diplomacy_relation_horrible, colors);
        }
        catch (Exception exception)
        {
            OblivionMode.Logger.LogWarning($"Could not render Oblivion opinion reasons: {exception}");
        }
    }
}

[HarmonyPatch(typeof(OpinionManager), nameof(OpinionManager.GetOpinion))]
internal static class OblivionOpinionValuePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void ApplyOblivionOpinion(
        GameState gameState,
        PlayerState playerState,
        byte opponent,
        ref float __result
    )
    {
        if (!gameState.TryGetPlayer(opponent, out PlayerState subject)) return;
        if (OblivionOpinions.TryGetForcedOpinion(gameState, playerState, subject, out float forced))
            __result = forced;
    }
}

[HarmonyPatch(typeof(PlayerState), nameof(PlayerState.GetOpinion))]
internal static class OblivionPlayerOpinionValuePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void ApplyOblivionOpinion(
        PlayerState __instance,
        GameState gameState,
        byte opponent,
        ref float __result
    )
    {
        if (!gameState.TryGetPlayer(opponent, out PlayerState subject)) return;
        if (OblivionOpinions.TryGetForcedOpinion(gameState, __instance, subject, out float forced))
            __result = forced;
    }
}

[HarmonyPatch(typeof(OpinionManager), nameof(OpinionManager.UpdateOpinions))]
internal static class OblivionOpinionStoragePatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void StoreOblivionOpinion(GameState gameState, PlayerState player)
    {
        OblivionOpinions.EnforceStoredOpinions(gameState, player);
    }
}

[HarmonyPatch(typeof(AI), nameof(AI.GetMove))]
internal static class OblivionAIMovePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void EnforceBeforeBotDecision(GameState gameState, PlayerState player)
    {
        OblivionOpinions.EnforceStoredOpinions(gameState, player);
    }
}

[HarmonyPatch(typeof(AI), nameof(AI.ShouldAcceptPeace))]
internal static class OblivionAIPeacePatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static bool EnforceOblivionPeaceResponse(
        GameState gameState,
        byte playerId,
        byte opponentId,
        ref bool __result
    )
    {
        if (!gameState.TryGetPlayer(playerId, out PlayerState observer) ||
            !gameState.TryGetPlayer(opponentId, out PlayerState subject) ||
            !OblivionOpinions.TryGetForcedOpinion(gameState, observer, subject, out float forced))
            return true;

        __result = forced > 0f;
        return false;
    }
}

[HarmonyPatch(typeof(AI), nameof(AI.AddPossibleDiplomacyCommands))]
internal static class OblivionAIDiplomacyCommandPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void FilterOblivionDiplomacy(
        GameState gameState,
        PlayerState player,
        Il2CppSystem.Collections.Generic.List<AI.ScoredCommand> possibleCommands
    )
    {
        if (!OblivionMode.IsActive(gameState) || !OblivionOpinions.IsBot(player) ||
            possibleCommands == null)
            return;

        for (int index = possibleCommands.Count - 1; index >= 0; index--)
        {
            AI.ScoredCommand scored = possibleCommands[index];
            if (scored == null || scored.command == null) continue;
            CommandBase command = scored.command;
            byte targetId;
            bool remove;

            if (command is PeaceTreatyCommand offerPeace)
            {
                targetId = offerPeace.OpponentId;
                remove = gameState.TryGetPlayer(targetId, out PlayerState target) &&
                         OblivionOpinions.IsLocal(target.Id);
            }
            else if (command is BreakPeaceCommand breakPeace)
            {
                targetId = breakPeace.OpponentId;
                remove = gameState.TryGetPlayer(targetId, out PlayerState target) &&
                         OblivionOpinions.IsBot(target);
            }
            else
            {
                continue;
            }

            if (remove) possibleCommands.RemoveAt(index);
        }
    }
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.GetLocalizedTopReasons))]
internal static class OblivionEnemyReasonLabelPatch
{
    private sealed record DisplayReason(string Label, float Value, Color Color, int Order);

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void PutEnemyFirst(
        PlayerInfoPopup __instance,
        Il2CppSystem.Collections.Generic.List<Il2CppSystem.Collections.Generic.KeyValuePair<OpinionManager.Type, float>> reasons,
        ref Il2CppSystem.Collections.Generic.Dictionary<string, Color> opinionColors,
        ref string __result
    )
    {
        PlayerState observer = __instance.player;
        PlayerState subject = GameManager.LocalPlayer;
        GameState state = GameManager.GameState;
        if (!OblivionOpinions.ShouldShowEnemyReason(observer, subject, state)) return;

        opinionColors ??= new Il2CppSystem.Collections.Generic.Dictionary<string, Color>();
        List<DisplayReason> candidates = new()
        {
            new DisplayReason(OblivionOpinions.EnemyLabel, -200f, Color.red, -1),
        };

        int order = 0;
        if (reasons != null)
        {
            foreach (var reason in reasons)
            {
                string label = OblivionOpinions.NativeLabel(reason.Key);
                if (string.IsNullOrWhiteSpace(label)) continue;
                Color color = reason.Value >= 0f ? Color.green : Color.red;
                if (opinionColors.TryGetValue(label, out Color nativeColor)) color = nativeColor;
                candidates.Add(new DisplayReason(label, reason.Value, color, order++));
            }
        }

        // The normal UI reserves space for three tags. Some early-game states
        // expose fewer than two non-zero native reasons, so use localized
        // zero-value native labels only as visual fillers after real reasons.
        foreach (OpinionManager.Type fallback in new[]
                 {
                     OpinionManager.Type.Like,
                     OpinionManager.Type.Powerful,
                     OpinionManager.Type.Peaceful,
                     OpinionManager.Type.Weak,
                     OpinionManager.Type.Intrusive,
                 })
        {
            if (candidates
                .Select(candidate => candidate.Label)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() >= 3)
                break;

            string label = OblivionOpinions.NativeLabel(fallback);
            if (string.IsNullOrWhiteSpace(label) ||
                candidates.Any(candidate =>
                    string.Equals(candidate.Label, label, StringComparison.OrdinalIgnoreCase)))
                continue;
            candidates.Add(new DisplayReason(label, 0f, Color.green, order++));
        }

        List<DisplayReason> selected = candidates
            .GroupBy(reason => reason.Label, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(reason => Math.Abs(reason.Value)).First())
            .OrderByDescending(reason => Math.Abs(reason.Value))
            .ThenBy(reason => reason.Order)
            .Take(3)
            .ToList();

        string prefix = SentencePrefix(__result, opinionColors);
        string joined = selected.Count switch
        {
            1 => selected[0].Label,
            2 => $"{selected[0].Label} and {selected[1].Label}",
            _ => $"{selected[0].Label}, {selected[1].Label} and {selected[2].Label}",
        };
        __result = $"{prefix}{joined}.";

        opinionColors.Clear();
        foreach (DisplayReason reason in selected) opinionColors[reason.Label] = reason.Color;
    }

    private static string SentencePrefix(
        string current,
        Il2CppSystem.Collections.Generic.Dictionary<string, Color> colors
    )
    {
        if (!string.IsNullOrWhiteSpace(current))
        {
            int firstReason = int.MaxValue;
            foreach (var item in colors)
            {
                if (string.IsNullOrWhiteSpace(item.Key)) continue;
                int position = current.IndexOf(item.Key, StringComparison.OrdinalIgnoreCase);
                if (position >= 0 && position < firstReason) firstReason = position;
            }
            if (firstReason != int.MaxValue) return current[..firstReason];
        }
        return "They think you are ";
    }
}

/// <summary>
/// PlayerInfoPopup asks vanilla to turn a numeric opinion into the localized
/// relation label. Force the input to the enemy value so it always renders the
/// native Horrible label, even if another UI path cached a pre-Oblivion total.
/// </summary>
[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.GetOpinionTextFromValue))]
internal static class OblivionPopupRelationTextPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    private static void ForceHorribleValue(PlayerInfoPopup __instance, ref float opinionValue)
    {
        PlayerState observer = __instance.player;
        PlayerState subject = GameManager.LocalPlayer;
        GameState state = GameManager.GameState;
        if (OblivionOpinions.ShouldShowEnemyReason(observer, subject, state))
            opinionValue = OblivionOpinions.PlayerEnemyPenalty;
    }
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.Refresh))]
internal static class OblivionPopupRelationSliderPatch
{
    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)]
    private static void ForceHorribleSlider(PlayerInfoPopup __instance)
    {
        PlayerState observer = __instance.player;
        PlayerState subject = GameManager.LocalPlayer;
        GameState state = GameManager.GameState;
        if (!OblivionOpinions.ShouldShowEnemyReason(observer, subject, state) ||
            __instance.relationSlider == null)
            return;

        __instance.relationSlider.value = __instance.relationSlider.minValue;
        OblivionOpinions.ShowEnemyInsight(__instance);
    }
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.SwapButtons))]
internal static class OblivionEnemyReasonButtonPatch
{
    [HarmonyPostfix]
    private static void AttachDescription(PlayerInfoPopup __instance)
    {
        foreach (UITextButton button in __instance.existingOpinionButtons)
        {
            if (!string.Equals(button.text, OblivionOpinions.EnemyLabel, StringComparison.OrdinalIgnoreCase))
                continue;
            button.ClearCallbacks();
            button.OnClickedSignal.Add(
                DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(OblivionOpinions.ShowEnemyDescription)
            );
        }
    }
}

[HarmonyPatch(typeof(UIButtonBase), nameof(UIButtonBase.OnPointerClick))]
internal static class OblivionEnemyReasonClickPatch
{
    [HarmonyPrefix]
    private static bool OpenEnemyDescription(UIButtonBase __instance)
    {
        if (__instance is not UITextButton button ||
            !string.Equals(button.text, OblivionOpinions.EnemyLabel, StringComparison.OrdinalIgnoreCase))
            return true;

        OblivionOpinions.ShowEnemyDescription();
        return false;
    }
}
