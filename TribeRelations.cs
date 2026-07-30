using HarmonyLib;
using I2.Loc;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace BetterBoPMod;

internal sealed class BetterBoPOpinionReason
{
    internal BetterBoPOpinionReason(string label, float value, string description)
    {
        Label = label;
        Value = value;
        Description = description;
    }

    internal string Label { get; }
    internal float Value { get; }
    internal string Description { get; }
}

/// <summary>
/// Calculates Better BoP opinion reasons from the tribe being judged. These
/// values are read live so diplomacy and score changes affect AI decisions and
/// the relation popup on the same turn.
/// </summary>
internal static class BetterBoPOpinions
{
    internal const string GenerousLabel = "generous";
    internal const string RespectedLabel = "respected";
    internal const string HatedLabel = "hated";
    internal const string EnemyLabel = "the enemy";

    internal static string DominatingLabel
    {
        get
        {
            string localized = ScriptLocalization.opinion_reason_winning;
            return string.IsNullOrWhiteSpace(localized) ? "dominating" : localized;
        }
    }

    internal static List<BetterBoPOpinionReason> GetReasons(
        GameState gameState,
        PlayerState observer,
        byte subjectId
    )
    {
        List<BetterBoPOpinionReason> result = new();
        if (observer == null || !gameState.TryGetPlayer(subjectId, out PlayerState subject)) return result;

        if (GiftStars.IsGenerous(gameState, observer.Id, subjectId))
        {
            result.Add(new BetterBoPOpinionReason(
                GenerousLabel,
                OpinionManager.LoveLimit,
                "You have shown kindness to them."
            ));
        }

        AddDiplomaticReputation(subject, result);

        float dominating = GetDominatingValue(gameState, subjectId);
        if (dominating != 0f)
        {
            result.Add(new BetterBoPOpinionReason(
                DominatingLabel,
                dominating,
                "Your tribe is dominating the score ranking."
            ));
        }

        if (OblivionMode.IsActive(gameState) && IsBot(observer) && IsLocal(subjectId))
        {
            result.Add(new BetterBoPOpinionReason(
                EnemyLabel,
                -200f,
                "You are the enemy."
            ));
        }

        return result;
    }

    private static void AddDiplomaticReputation(
        PlayerState subject,
        List<BetterBoPOpinionReason> result
    )
    {
        int peaceTreaties = 0;
        int wars = 0;
        foreach (var relation in subject.relations)
        {
            if (relation.Value.State == DiplomacyRelationState.Peace) peaceTreaties++;
            else if (relation.Value.State == DiplomacyRelationState.War) wars++;
        }

        // The two-active-treaty threshold follows the requested 2 peace / 1 war
        // example: the difference contributes +5 once at least two treaties are
        // active. Hated mirrors the same rule for wars.
        int balance = peaceTreaties - wars;
        if (peaceTreaties >= 2 && balance > 0)
        {
            result.Add(new BetterBoPOpinionReason(
                RespectedLabel,
                Math.Min(20, balance * 5),
                "You are well connected with other tribes."
            ));
        }
        else if (wars >= 2 && balance < 0)
        {
            result.Add(new BetterBoPOpinionReason(
                HatedLabel,
                Math.Max(-20, balance * 5),
                "You are infamous among other tribes."
            ));
        }
    }

    internal static float GetDominatingValue(GameState gameState, byte subjectId)
    {
        if (gameState.CurrentTurn < 6 || gameState.PlayerCount < 3) return 0f;

        int penalizedRanks = gameState.PlayerCount switch
        {
            <= 5 => 1,
            <= 10 => 2,
            _ => 3,
        };

        List<PlayerState> ranked = new();
        foreach (PlayerState player in gameState.PlayerStates) ranked.Add(player);
        ranked.Sort((left, right) =>
        {
            int scoreOrder = right.score.CompareTo(left.score);
            return scoreOrder != 0 ? scoreOrder : left.Id.CompareTo(right.Id);
        });

        int rank = ranked.FindIndex(player => player.Id == subjectId);
        if (rank < 0 || rank >= penalizedRanks) return 0f;
        return rank switch
        {
            0 => -100f,
            1 => -50f,
            2 => -25f,
            _ => 0f,
        };
    }

    internal static bool IsBot(PlayerState player) => !player.AccountId.HasValue;

    internal static float RemoveStoredVanillaWinning(PlayerState observer, byte subjectId)
    {
        if (observer?.opinions == null ||
            !observer.opinions.Opinions.TryGetValue(subjectId, out OpinionState opinion))
        {
            return 0f;
        }

        float winning = opinion.GetOpinion(OpinionManager.Type.Winning);
        if (Math.Abs(winning) < 0.001f) return 0f;
        opinion.reasons[OpinionManager.Type.Winning] = 0f;
        opinion.total -= winning;
        return winning;
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

    internal static bool TryGetDescription(string label, out string description)
    {
        description = string.Empty;
        if (string.Equals(label, GenerousLabel, StringComparison.OrdinalIgnoreCase))
            description = "You have shown kindness to them.";
        else if (string.Equals(label, RespectedLabel, StringComparison.OrdinalIgnoreCase))
            description = "You are well connected with other tribes.";
        else if (string.Equals(label, HatedLabel, StringComparison.OrdinalIgnoreCase))
            description = "You are infamous among other tribes.";
        else if (string.Equals(label, DominatingLabel, StringComparison.OrdinalIgnoreCase))
            description = "Your tribe is dominating the score ranking.";
        else if (string.Equals(label, EnemyLabel, StringComparison.OrdinalIgnoreCase))
            description = "You are the enemy.";
        return description.Length > 0;
    }

    internal static void ShowDescription(string label)
    {
        if (!TryGetDescription(label, out string description)) return;
        BasicPopup popup = PopupManager.GetBasicPopup().SetHeader(label).SetDescription(description);
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
}

/// <summary>Removes the original opaque winning-score calculation.</summary>
[HarmonyPatch(typeof(OpinionManager), nameof(OpinionManager.GetWinHateForPlayer))]
internal static class DisableVanillaDominatingPatch
{
    [HarmonyPrefix]
    private static bool RemoveVanillaWinningPenalty(ref float __result)
    {
        __result = 0f;
        return false;
    }
}

[HarmonyPatch(typeof(OpinionManager), nameof(OpinionManager.UpdateOpinion))]
internal static class ClearStoredVanillaDominatingPatch
{
    [HarmonyPostfix]
    private static void RemoveWinningFromUpdatedState(PlayerState player, PlayerState opponent)
    {
        BetterBoPOpinions.RemoveStoredVanillaWinning(player, opponent.Id);
    }
}

[HarmonyPatch(typeof(OpinionManager), nameof(OpinionManager.GetOpinion))]
internal static class BetterBoPOpinionValuePatch
{
    [HarmonyPostfix]
    private static void AddBetterBoPReasons(
        GameState gameState,
        PlayerState playerState,
        byte opponent,
        ref float __result
    )
    {
        // Also clean a cached pre-update value so games saved on an older mod
        // version do not carry the legacy Winning penalty forward.
        __result -= BetterBoPOpinions.RemoveStoredVanillaWinning(playerState, opponent);
        List<BetterBoPOpinionReason> reasons = BetterBoPOpinions.GetReasons(gameState, playerState, opponent);

        // Generous retains its original max-positive behavior. Other reasons are
        // then added at their stated values so they influence actual AI choices.
        BetterBoPOpinionReason? generous = reasons.FirstOrDefault(reason =>
            string.Equals(reason.Label, BetterBoPOpinions.GenerousLabel, StringComparison.OrdinalIgnoreCase)
        );
        if (generous != null) __result = Math.Min(OpinionManager.LoveLimit, __result + generous.Value);

        foreach (BetterBoPOpinionReason reason in reasons)
        {
            if (ReferenceEquals(reason, generous)) continue;
            __result += reason.Value;
        }

        if (!gameState.TryGetPlayer(opponent, out PlayerState subject)) return;
        if (!OblivionMode.IsActive(gameState) || !BetterBoPOpinions.IsBot(playerState)) return;

        if (BetterBoPOpinions.IsBot(subject))
        {
            // +200 is the Oblivion alliance bonus. The lower bound guarantees
            // the bots still love one another after any other active negatives.
            __result = Math.Max(OpinionManager.LoveLimit, __result + 200f);
        }
        else if (BetterBoPOpinions.IsLocal(subject.Id))
        {
            __result = Math.Min(OpinionManager.HateLimit, __result);
        }
    }
}

[HarmonyPatch(typeof(OpinionManager), nameof(OpinionManager.GetReasons))]
internal static class RemoveVanillaDominatingReasonPatch
{
    [HarmonyPostfix]
    private static void RemoveWinningReason(
        Il2CppSystem.Collections.Generic.List<Il2CppSystem.Collections.Generic.KeyValuePair<OpinionManager.Type, float>> __result
    )
    {
        if (__result == null) return;
        for (int index = __result.Count - 1; index >= 0; index--)
        {
            if (__result[index].Key == OpinionManager.Type.Winning) __result.RemoveAt(index);
        }
    }
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.GetLocalizedTopReasons))]
internal static class BetterBoPOpinionReasonLabelPatch
{
    private sealed class DisplayReason
    {
        internal DisplayReason(string label, float value, Color color, bool custom, int order)
        {
            Label = label;
            Value = value;
            Color = color;
            Custom = custom;
            Order = order;
        }

        internal string Label { get; }
        internal float Value { get; }
        internal Color Color { get; }
        internal bool Custom { get; }
        internal int Order { get; }
    }

    [HarmonyPostfix]
    private static void RebuildTopThreeReasons(
        PlayerInfoPopup __instance,
        Il2CppSystem.Collections.Generic.List<Il2CppSystem.Collections.Generic.KeyValuePair<OpinionManager.Type, float>> reasons,
        ref Il2CppSystem.Collections.Generic.Dictionary<string, Color> opinionColors,
        ref string __result
    )
    {
        PlayerState viewed = __instance.player;
        PlayerState local = GameManager.LocalPlayer;
        GameState gameState = GameManager.GameState;
        if (viewed == null || local == null || gameState == null) return;

        List<BetterBoPOpinionReason> customReasons = BetterBoPOpinions.GetReasons(gameState, viewed, local.Id);
        bool hasVanillaWinning = false;
        if (reasons != null)
        {
            foreach (var reason in reasons)
            {
                if (reason.Key != OpinionManager.Type.Winning) continue;
                hasVanillaWinning = true;
                break;
            }
        }
        if (customReasons.Count == 0 && !hasVanillaWinning) return;

        opinionColors ??= new Il2CppSystem.Collections.Generic.Dictionary<string, Color>();
        List<DisplayReason> candidates = new();
        int order = 0;
        if (reasons != null)
        {
            foreach (var reason in reasons)
            {
                if (reason.Key == OpinionManager.Type.Winning || Math.Abs(reason.Value) < 0.001f) continue;
                string label = GetNativeLabel(reason.Key);
                if (string.IsNullOrWhiteSpace(label)) continue;
                Color color = reason.Value >= 0 ? Color.green : Color.red;
                if (opinionColors.TryGetValue(label, out Color nativeColor)) color = nativeColor;
                candidates.Add(new DisplayReason(label, reason.Value, color, false, order++));
            }
        }

        Color positiveColor = FindColor(candidates, true, Color.green);
        Color negativeColor = FindColor(candidates, false, Color.red);
        foreach (BetterBoPOpinionReason reason in customReasons)
        {
            candidates.Add(new DisplayReason(
                reason.Label,
                reason.Value,
                reason.Value >= 0 ? positiveColor : negativeColor,
                true,
                order++
            ));
        }

        List<DisplayReason> selected = candidates
            .GroupBy(candidate => candidate.Label, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(candidate => Math.Abs(candidate.Value)).First())
            .OrderByDescending(candidate => Math.Abs(candidate.Value))
            .ThenByDescending(candidate => candidate.Custom)
            .ThenBy(candidate => candidate.Order)
            .Take(3)
            .ToList();
        if (selected.Count == 0) return;

        string prefix = GetSentencePrefix(__result, opinionColors);
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

    private static Color FindColor(List<DisplayReason> reasons, bool positive, Color fallback)
    {
        DisplayReason? match = reasons.FirstOrDefault(reason => positive ? reason.Value >= 0 : reason.Value < 0);
        return match?.Color ?? fallback;
    }

    private static string GetSentencePrefix(
        string result,
        Il2CppSystem.Collections.Generic.Dictionary<string, Color> colors
    )
    {
        if (!string.IsNullOrWhiteSpace(result))
        {
            int first = int.MaxValue;
            foreach (var entry in colors)
            {
                if (string.IsNullOrWhiteSpace(entry.Key)) continue;
                int position = result.IndexOf(entry.Key, StringComparison.OrdinalIgnoreCase);
                if (position >= 0 && position < first) first = position;
            }
            if (first != int.MaxValue) return result[..first];
        }
        return "They think you are ";
    }

    private static string GetNativeLabel(OpinionManager.Type type) => type switch
    {
        OpinionManager.Type.CommonRelation => ScriptLocalization.opinion_reason_commonrelation,
        OpinionManager.Type.DifferentRelation => ScriptLocalization.opinion_reason_differentrelation,
        OpinionManager.Type.Peaceful => ScriptLocalization.opinion_reason_peaceful,
        OpinionManager.Type.Aggression => ScriptLocalization.opinion_reason_aggression,
        OpinionManager.Type.Embassy => ScriptLocalization.opinion_reason_embassy,
        OpinionManager.Type.Threatening => ScriptLocalization.opinion_reason_threatening,
        OpinionManager.Type.Intrusive => ScriptLocalization.opinion_reason_intrusive,
        OpinionManager.Type.Brave => ScriptLocalization.opinion_reason_brave,
        OpinionManager.Type.Dislike => ScriptLocalization.opinion_reason_dislike,
        OpinionManager.Type.Like => ScriptLocalization.opinion_reason_like,
        OpinionManager.Type.Weak => ScriptLocalization.opinion_reason_weak,
        OpinionManager.Type.Powerful => ScriptLocalization.opinion_reason_powerful,
        _ => string.Empty,
    };
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.SwapButtons))]
internal static class BetterBoPOpinionReasonButtonPatch
{
    [HarmonyPostfix]
    private static void AttachCustomReasonPopups(PlayerInfoPopup __instance)
    {
        foreach (UITextButton button in __instance.existingOpinionButtons)
        {
            string label = button.text;
            if (!BetterBoPOpinions.TryGetDescription(label, out _)) continue;
            button.ClearCallbacks();
            button.OnClickedSignal.Add(
                DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => BetterBoPOpinions.ShowDescription(label))
            );
        }
    }
}

// SwapButtons can finish animating after its postfix. Intercepting the final
// pointer click keeps every custom pill reliable without changing native pills.
[HarmonyPatch(typeof(UIButtonBase), nameof(UIButtonBase.OnPointerClick))]
internal static class BetterBoPOpinionReasonClickPatch
{
    [HarmonyPrefix]
    private static bool OpenCustomReason(UIButtonBase __instance)
    {
        if (__instance is not UITextButton button ||
            !BetterBoPOpinions.TryGetDescription(button.text, out _))
        {
            return true;
        }

        BetterBoPOpinions.ShowDescription(button.text);
        return false;
    }
}
