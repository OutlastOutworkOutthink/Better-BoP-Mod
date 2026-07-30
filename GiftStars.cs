using BepInEx.Logging;
using HarmonyLib;
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
            ShowMessage("Gift Received", $"{from.UserName} gifted you {gift.Amount} stars. After the 20% transfer cost, you receive {received} stars.", $"Collect {received} ⭐");
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
            .SetDescription("Choose 5, 10, or 20 stars. The receiving tribe gets 80% of the amount sent.");
        int[] amounts = { 5, 10, 20 };
        Il2CppReferenceArray<PopupBase.PopupButtonData> buttons = new(amounts.Length + 1);
        for (int index = 0; index < amounts.Length; index++)
        {
            int amount = amounts[index];
            bool affordable = local.Currency >= amount;
            buttons[index] = new PopupBase.PopupButtonData
            {
                id = amount,
                text = $"⭐ {amount} → {amount * 4 / 5}",
                state = affordable ? PopupBase.PopupButtonData.States.None : PopupBase.PopupButtonData.States.Disabled,
                closesPopup = true,
                callback = affordable
                    ? DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => ShowConfirmation(target, amount))
                    : DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => { }),
            };
        }
        buttons[amounts.Length] = new PopupBase.PopupButtonData
        {
            id = 0,
            text = "Cancel",
            state = PopupBase.PopupButtonData.States.Alternative,
            closesPopup = true,
            callback = DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => { }),
        };
        popup.SetButtonData(buttons, true);
        popup.Show();
    }

    private static void ShowConfirmation(PlayerState target, int amount)
    {
        BasicPopup popup = PopupManager.GetBasicPopup()
            .SetHeader("Confirm Star Gift")
            .SetDescription($"Send {amount} stars to {target.UserName}? They will receive {amount * 4 / 5} stars.");
        popup.SetMainButton(
            $"Gift {amount} ⭐",
            DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => BeginGift(target.Id, amount))
        );
        popup.SetSecondaryButton("Cancel", DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => { }));
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
            button.ShowIconAndTextContainer(__instance.embassyIncome.icon.sprite, "Gift Stars");
            button.SetText("Gift Stars");
            button.buttonActive = state.CurrentPlayer == local.Id;
            button.buttonExpensive = false;
            button.OnClickedSignal.Add(
                DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => GiftStars.ShowAmountPicker(player))
            );
            __instance.actionButtons.Add(button);
        }
        catch (Exception exception)
        {
            BetterBoPRules.Logger.LogError($"Could not add Gift Stars action: {exception}");
        }
    }
}

[HarmonyPatch(typeof(OpinionManager), nameof(OpinionManager.UpdateOpinion))]
internal static class GenerousOpinionPatch
{
    [HarmonyPostfix]
    private static void AddGenerousReason(OpinionManager __instance, GameState gameState, PlayerState player, PlayerState opponent)
    {
        if (!GiftStars.IsGenerous(gameState, player.Id, opponent.Id)) return;
        if (__instance.Opinions.TryGetValue(opponent.Id, out OpinionState opinion))
        {
            opinion.AddOpinion(OpinionManager.LoveLimit, OpinionManager.Type.Like);
        }
    }
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.Refresh))]
internal static class GenerousLabelPatch
{
    [HarmonyPostfix]
    private static void ShowGenerousTag(PlayerInfoPopup __instance)
    {
        PlayerState viewed = __instance.player;
        PlayerState local = GameManager.LocalPlayer;
        if (viewed == null || local == null || !GiftStars.IsGenerous(GameManager.GameState, viewed.Id, local.Id)) return;
        if (!__instance.opinionText.text.Contains("Generous", StringComparison.Ordinal))
        {
            __instance.opinionText.text += "\n<color=#4FCB71>Generous</color> (2 turns)";
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
        if (!state.GameLogicData.IsUnlocked(TechData.Type.Diplomacy, local)) return;
        if (local.relations.TryGetValue(viewed.Id, out DiplomacyRelation relation))
        {
            __instance.embassyIncome.Amount = Math.Max(0, relation.EmbassyLevel) * 2;
        }
    }
}
