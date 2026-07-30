using BepInEx.Logging;
using HarmonyLib;
using I2.Loc;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Polytopia.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine;

namespace BetterBoPMod;

internal static class GiftStars
{
    private static readonly byte[] Magic = Encoding.UTF8.GetBytes("BBP-GIFT-1\n");
    private static readonly Dictionary<string, uint> GenerousUntilTurn = new();
    private static ManualLogSource logger = null!;
    private static bool giftInFlight;

    internal static void Initialize(ManualLogSource logSource) => logger = logSource;

    internal sealed class GiftEnvelope
    {
        [JsonPropertyName("from")] public byte FromPlayerId { get; init; }
        [JsonPropertyName("to")] public byte ToPlayerId { get; init; }
        [JsonPropertyName("amount")] public int Amount { get; init; }
        [JsonPropertyName("turn")] public uint Turn { get; init; }
    }

    internal static byte[] Serialize(GiftEnvelope gift)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(gift);
        byte[] payload = new byte[Magic.Length + json.Length];
        Buffer.BlockCopy(Magic, 0, payload, 0, Magic.Length);
        Buffer.BlockCopy(json, 0, payload, Magic.Length, json.Length);
        return payload;
    }

    internal static bool TryDeserialize(byte[] payload, out GiftEnvelope? gift)
    {
        gift = null;
        if (payload.Length <= Magic.Length || !payload.AsSpan(0, Magic.Length).SequenceEqual(Magic)) return false;
        gift = JsonSerializer.Deserialize<GiftEnvelope>(payload.AsSpan(Magic.Length));
        return gift != null;
    }

    internal static bool CanOffer(GameState gameState, PlayerState from, PlayerState to, out string error)
    {
        if (from.Id == to.Id)
        {
            error = "Choose another tribe.";
            return false;
        }
        if (gameState.CurrentPlayer != from.Id)
        {
            error = "You can only gift stars during your own turn.";
            return false;
        }
        if (!gameState.GameLogicData.IsUnlocked(TechData.Type.Shields, from))
        {
            error = "Research Strategy before gifting stars.";
            return false;
        }
        error = string.Empty;
        return true;
    }

    internal static void ApplyGift(GameState gameState, GiftEnvelope gift, bool submittedLocally)
    {
        if (gift.Amount is not (5 or 10 or 20)) throw new InvalidOperationException("Gift amount must be 5, 10, or 20 stars.");
        if (!gameState.TryGetPlayer(gift.FromPlayerId, out PlayerState from) ||
            !gameState.TryGetPlayer(gift.ToPlayerId, out PlayerState to))
            throw new InvalidOperationException("Gift players are not in this game.");
        if (!CanOffer(gameState, from, to, out string error)) throw new InvalidOperationException(error);
        if (from.Currency < gift.Amount) throw new InvalidOperationException("The gifting tribe no longer has enough stars.");

        int received = gift.Amount * 4 / 5;
        from.Currency -= gift.Amount;
        to.Currency += received;
        ResourceEvents.ResourceRemoved(from.Id, ResourceManager.Type.Currency, gift.Amount, from.Currency);
        ResourceEvents.ResourceAdded(to.Id, ResourceManager.Type.Currency, received, to.Currency);
        ResourceEvents.RefreshWallets(from.Id);
        ResourceEvents.RefreshWallets(to.Id);

        // Account-less players are bots. Their positive relation is added by
        // GenerousOpinionPatch for two turns, at the engine's positive limit.
        if (!to.AccountId.HasValue)
        {
            string key = GenerousKey(gameState, to.Id, from.Id);
            uint until = gameState.CurrentTurn + 2;
            GenerousUntilTurn[key] = until;
            PlayerPrefs.SetInt($"bbp.generous.{key}", checked((int)until));
            PlayerPrefs.Save();
            to.MarkOpinionsDirty();
        }

        if (submittedLocally)
        {
            ShowMessage("Stars Gifted", $"You sent {gift.Amount} stars to {to.UserName}. They received {received} stars.", "Done");
        }
        else if (GameManager.IsPlayerLocal(to.Id))
        {
            ShowMessage("Gift Received", $"{from.UserName} gifted you {gift.Amount} stars. After the 20% transfer cost, you receive {received} stars.", $"Collect {received} stars");
        }
    }

    internal static bool IsGenerous(GameState gameState, byte receivingPlayerId, byte giftingPlayerId)
    {
        string key = GenerousKey(gameState, receivingPlayerId, giftingPlayerId);
        if (!GenerousUntilTurn.TryGetValue(key, out uint until))
        {
            string preferenceKey = $"bbp.generous.{key}";
            if (!PlayerPrefs.HasKey(preferenceKey)) return false;
            until = checked((uint)PlayerPrefs.GetInt(preferenceKey));
            GenerousUntilTurn[key] = until;
        }
        if (gameState.CurrentTurn <= until) return true;
        GenerousUntilTurn.Remove(key);
        PlayerPrefs.DeleteKey($"bbp.generous.{key}");
        PlayerPrefs.Save();
        return false;
    }

    private static string GenerousKey(GameState state, byte receiver, byte giver)
    {
        string game = IntegratedMultiplayer.ActiveGameId;
        if (string.IsNullOrWhiteSpace(game)) game = state.Seed.ToString();
        return $"{game}:{receiver}:{giver}";
    }

    internal static void ShowAmountPicker(PlayerState target)
    {
        GameState state = GameManager.GameState;
        PlayerState local = GameManager.LocalPlayer;
        if (!CanOffer(state, local, target, out string error))
        {
            ShowMessage("Gift Stars", error, "Okay");
            return;
        }

        BasicPopup popup = PopupManager.GetBasicPopup()
            .SetHeader($"Gift stars to {target.UserName}")
            .SetDescription(string.Empty);

        // This intentionally mirrors the city's level-up reward layout: three
        // evenly spaced, circular choices with an icon and a short label.
        UIBasicComponent choiceRow = UILibrary.NewEmptyComponent(popup.content);
        choiceRow.SetSize(720f, 210f);
        int[] amounts = { 5, 10, 20 };
        for (int index = 0; index < amounts.Length; index++)
        {
            int amount = amounts[index];
            bool affordable = local.Currency >= amount;
            UIRoundButton_UI2 choice = UILibrary.NewRoundButton(choiceRow.rectTransform)
                .SetStyle(UIButtonBase_UI2.ButtonStyle.Default)
                .SetButtonSize(UIRoundButton_UI2.ButtonSize.Large)
                .SetSprite(SpriteRef.UI_STARICON, 0.62f);
            choice.Text = $"{amount} stars";
            choice.SetPosition((index - 1) * 220f, 0f);
            choice.ButtonEnabled = affordable;
            choice.OnClickedSignal.Clear();
            if (affordable)
            {
                choice.OnClickedSignal.Add(
                    DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() =>
                    {
                        popup.Hide();
                        ShowConfirmation(target, amount);
                    })
                );
            }
            choice.UpdateLabelVisibility();
            choice.RunLayout();
        }

        popup.SetDynamicContent(choiceRow);
        Il2CppReferenceArray<PopupBase.PopupButtonData> buttons = new(1);
        buttons[0] = new PopupBase.PopupButtonData
        {
            id = 0,
            text = "Cancel",
            state = PopupBase.PopupButtonData.States.Alternative,
            closesPopup = true,
            callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => { }),
        };
        popup.SetButtonData(buttons, false);
        popup.RunLayout();
        popup.Show();
    }

    private static void ShowConfirmation(PlayerState target, int amount)
    {
        BasicPopup popup = PopupManager.GetBasicPopup()
            .SetHeader("Confirm Star Gift")
            .SetDescription($"Send {amount} stars to {target.UserName}?");
        Il2CppReferenceArray<PopupBase.PopupButtonData> buttons = new(2);
        buttons[0] = new PopupBase.PopupButtonData
        {
            id = 0,
            text = "Cancel",
            state = PopupBase.PopupButtonData.States.Alternative,
            closesPopup = true,
            callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => { }),
        };
        buttons[1] = new PopupBase.PopupButtonData
        {
            id = amount,
            text = $"Gift {amount} stars",
            state = PopupBase.PopupButtonData.States.None,
            closesPopup = true,
            callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => BeginGift(target.Id, amount)),
        };
        popup.SetButtonData(buttons, false);
        popup.RunLayout();
        popup.Show();
    }

    private static async void BeginGift(byte targetId, int amount)
    {
        if (giftInFlight) return;
        giftInFlight = true;
        try
        {
            GameState state = GameManager.GameState;
            PlayerState local = GameManager.LocalPlayer;
            if (!state.TryGetPlayer(targetId, out PlayerState target)) throw new InvalidOperationException("That tribe is no longer available.");
            if (!CanOffer(state, local, target, out string error)) throw new InvalidOperationException(error);
            if (local.Currency < amount) throw new InvalidOperationException("You no longer have enough stars for that gift.");

            GiftEnvelope gift = new()
            {
                FromPlayerId = local.Id,
                ToPlayerId = target.Id,
                Amount = amount,
                Turn = state.CurrentTurn,
            };
            if (IntegratedMultiplayer.Active)
            {
                await IntegratedMultiplayer.SubmitGiftAsync(gift).ConfigureAwait(false);
            }
            else if (GameManager.IsPlayerLocal(target.Id) || !target.AccountId.HasValue)
            {
                ApplyGift(state, gift, true);
            }
            else
            {
                throw new InvalidOperationException("Human multiplayer star gifts are available only in bot-hosted Integrated games.");
            }
        }
        catch (Exception exception)
        {
            logger.LogError($"Gift Stars failed: {exception}");
            DiscordIntegrationPatch.RunOnMainThread(() => ShowMessage("Gift Failed", exception.Message, "Okay"));
        }
        finally
        {
            giftInFlight = false;
        }
    }

    internal static void ShowGenerousInfo()
    {
        ShowMessage(
            "generous",
            "You have showed kindness to them.",
            "Okay"
        );
    }

    private static void ShowMessage(string header, string description, string button)
    {
        BasicPopup popup = PopupManager.GetBasicPopup().SetHeader(header).SetDescription(description);
        popup.SetMainButton(button, DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => { }));
        popup.Show();
    }
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.UpdateDiplomacyActionButtons))]
internal static class GiftStarsButtonPatch
{
    [HarmonyPostfix]
    private static void AddGiftStarsButton(PlayerInfoPopup __instance, PlayerState player)
    {
        try
        {
            PlayerState local = GameManager.LocalPlayer;
            GameState state = GameManager.GameState;
            if (local == null || player == null || local.Id == player.Id) return;
            if (!state.GameLogicData.IsUnlocked(TechData.Type.Shields, local)) return;

            UIRoundButton button = UnityEngine.Object.Instantiate(
                __instance.roundButtonPrefab,
                __instance.actionButtonContainer
            );
            button.gameObject.SetActive(true);
            button.SetText("Gift Stars");
            button.buttonActive = state.CurrentPlayer == local.Id;
            button.buttonExpensive = false;
            button.OnClickedSignal.Add(
                DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() =>
                {
                    __instance.Hide();
                    GiftStars.ShowAmountPicker(player);
                })
            );
            __instance.actionButtons.Add(button);

            try
            {
                UnityEngine.Sprite star = GameManager.GetSpriteAtlasManager().GetSprite(SpriteRef.UI_STARICON);
                if (star != null) button.SetSprite(star, false);
                else button.SetSprite(__instance.embassyIncome.icon.sprite, false);
            }
            catch (Exception iconException)
            {
                button.SetSprite(__instance.embassyIncome.icon.sprite, false);
                BetterBoPRules.Logger.LogWarning($"Gift Stars icon fallback used: {iconException.Message}");
            }
        }
        catch (Exception exception)
        {
            BetterBoPRules.Logger.LogError($"Could not add Gift Stars action: {exception}");
        }
    }
}

[HarmonyPatch(typeof(OpinionManager), nameof(OpinionManager.GetOpinion))]
internal static class GenerousOpinionPatch
{
    [HarmonyPostfix]
    private static void AddGenerousBoon(GameState gameState, PlayerState playerState, byte opponent, ref float __result)
    {
        if (!GiftStars.IsGenerous(gameState, playerState.Id, opponent)) return;
        __result = Math.Min(OpinionManager.LoveLimit, __result + OpinionManager.LoveLimit);
    }
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.GetLocalizedTopReasons))]
internal static class GenerousReasonLabelPatch
{
    [HarmonyPostfix]
    private static void UseGenerousReasonLabel(
        PlayerInfoPopup __instance,
        Il2CppSystem.Collections.Generic.List<Il2CppSystem.Collections.Generic.KeyValuePair<OpinionManager.Type, float>> reasons,
        ref Il2CppSystem.Collections.Generic.Dictionary<string, Color> opinionColors,
        ref string __result
    )
    {
        PlayerState viewed = __instance.player;
        PlayerState local = GameManager.LocalPlayer;
        if (viewed == null || local == null || !GiftStars.IsGenerous(GameManager.GameState, viewed.Id, local.Id)) return;

        string original = ScriptLocalization.opinion_reason_like;
        const string generous = "generous";
        if (__result?.Contains(generous, StringComparison.OrdinalIgnoreCase) ?? false) return;

        __result = string.IsNullOrWhiteSpace(__result)
            ? generous
            : $"{__result.TrimEnd().TrimEnd('.')} and {generous}.";
        if (opinionColors != null)
        {
            Color color = Color.green;
            if (!string.IsNullOrWhiteSpace(original) && opinionColors.TryGetValue(original, out Color likeColor))
            {
                color = likeColor;
            }
            opinionColors[generous] = color;
        }
    }
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.SwapButtons))]
internal static class GenerousReasonButtonPatch
{
    [HarmonyPostfix]
    private static void AttachGenerousPopup(PlayerInfoPopup __instance)
    {
        foreach (UITextButton button in __instance.existingOpinionButtons)
        {
            if (!string.Equals(button.text, "generous", StringComparison.OrdinalIgnoreCase)) continue;
            button.OnClickedSignal.Clear();
            button.OnClickedSignal.Add(
                DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(GiftStars.ShowGenerousInfo)
            );
            break;
        }
    }
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.Refresh))]
internal static class EmbassyIncomeDisplayPatch
{
    [HarmonyPostfix]
    private static void ShowResearchedDiplomacyIncome(PlayerInfoPopup __instance)
    {
        PlayerState viewed = __instance.player;
        PlayerState local = GameManager.LocalPlayer;
        GameState state = GameManager.GameState;
        if (viewed == null || local == null || __instance.embassyIncome == null) return;
        bool hasDiplomacy = state.GameLogicData.IsUnlocked(TechData.Type.Diplomacy, local);
        __instance.embassyInfoText.text = hasDiplomacy
            ? "Diplomacy doubles embassy income: 2 stars per turn, or 4 during peace. This applies to all current and future embassies."
            : "Embassy income: 1 star per turn, or 2 during peace. Research Diplomacy to double all current and future embassy income.";
        if (local.relations.TryGetValue(viewed.Id, out DiplomacyRelation relation))
        {
            int multiplier = hasDiplomacy ? 2 : 1;
            __instance.embassyIncome.Amount = Math.Max(0, relation.EmbassyLevel) * multiplier;
        }
    }
}
