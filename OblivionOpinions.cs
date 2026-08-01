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
        return player != null &&
               player.Id != PlayerState.NATURE_PLAYER_ID &&
               !player.AccountId.HasValue;
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
}

[HarmonyPatch(typeof(OpinionManager), nameof(OpinionManager.GetOpinion))]
internal static class OblivionOpinionValuePatch
{
    [HarmonyPostfix]
    private static void ApplyOblivionOpinion(
        GameState gameState,
        PlayerState playerState,
        byte opponent,
        ref float __result
    )
    {
        if (!OblivionMode.IsActive(gameState) || !OblivionOpinions.IsBot(playerState)) return;
        if (!gameState.TryGetPlayer(opponent, out PlayerState subject)) return;

        if (OblivionOpinions.IsBot(subject))
        {
            // Exactly +200 "charming", with a love floor so ordinary negative
            // observations can never split the Oblivion bot alliance.
            __result = Math.Max(OpinionManager.LoveLimit, __result + OblivionOpinions.BotAllianceBonus);
        }
        else if (OblivionOpinions.IsLocal(subject.Id))
        {
            // Exactly -200 "the enemy", with a hate ceiling so ordinary boons
            // can never make an Oblivion bot friendly toward the player.
            __result = Math.Min(OpinionManager.HateLimit, __result + OblivionOpinions.PlayerEnemyPenalty);
        }
    }
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.GetLocalizedTopReasons))]
internal static class OblivionEnemyReasonLabelPatch
{
    private sealed record DisplayReason(string Label, float Value, Color Color, int Order);

    [HarmonyPostfix]
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
                if (Math.Abs(reason.Value) < 0.001f) continue;
                string label = OblivionOpinions.NativeLabel(reason.Key);
                if (string.IsNullOrWhiteSpace(label)) continue;
                Color color = reason.Value >= 0f ? Color.green : Color.red;
                if (opinionColors.TryGetValue(label, out Color nativeColor)) color = nativeColor;
                candidates.Add(new DisplayReason(label, reason.Value, color, order++));
            }
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
