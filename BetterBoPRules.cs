using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Polytopia.Data;
using PolytopiaBackendBase.Common;
using System.Reflection;
using UnityEngine;

namespace BetterBoPMod;

/// <summary>
/// Centralizes rules that must be identical in local and Integrated games.
/// Polytopia can replace its GameLogicData while opening an online state, so
/// this method is deliberately safe to run more than once.
/// </summary>
internal static class BetterBoPRules
{
    internal static ManualLogSource Logger { get; set; } = null!;

    internal static void Apply(GameLogicData data)
    {
        TechData basic = data.GetTechData(TechData.Type.Basic);
        TechData strategy = data.GetTechData(TechData.Type.Shields);
        TechData diplomacy = data.GetTechData(TechData.Type.Diplomacy);
        TechData meditation = data.GetTechData(TechData.Type.Meditation);
        TechData philosophy = data.GetTechData(TechData.Type.Philosophy);

        MoveAbility(PlayerAbility.Type.Embassy, diplomacy, strategy);
        MoveAbility(PlayerAbility.Type.CapitalVision, diplomacy, strategy);

        // Keep peace off the visible technology tree, but attach it to the
        // universally unlocked hidden Basic technology. PeaceTreatyCommand
        // validates against the unlocked-abilities collection, so a UI-only
        // IsUnlocked override is not sufficient.
        foreach (var tech in data.AllTechData)
        {
            RemoveAbility(tech.Value, PlayerAbility.Type.PeaceTreaty);
        }
        if (!basic.abilityUnlocks.Contains(PlayerAbility.Type.PeaceTreaty))
        {
            basic.abilityUnlocks.Add(PlayerAbility.Type.PeaceTreaty);
        }

        UnitData mindBender = data.GetUnitData(UnitData.Type.MindBender);
        while (philosophy.unitUnlocks.Contains(mindBender)) philosophy.unitUnlocks.Remove(mindBender);
        if (!meditation.unitUnlocks.Contains(mindBender)) meditation.unitUnlocks.Add(mindBender);

        TribeData aimo = data.GetTribeData(TribeType.Aimo);
        aimo.startingUnit = mindBender;
        aimo.startingTech.Clear();
        aimo.startingTech.Add(meditation);

        // Strategy embassies start at one star per level (one normally, two
        // during peace). DiplomacyEmbassyIncomePatch doubles the final value.
        data.DiplomacyData.embassyIncome = 1;
    }

    private static void MoveAbility(PlayerAbility.Type ability, TechData from, TechData to)
    {
        RemoveAbility(from, ability);
        if (!to.abilityUnlocks.Contains(ability)) to.abilityUnlocks.Add(ability);
    }

    private static void RemoveAbility(TechData tech, PlayerAbility.Type ability)
    {
        while (tech.abilityUnlocks.Contains(ability)) tech.abilityUnlocks.Remove(ability);
    }
}

[HarmonyPatch(typeof(GameLogicData), nameof(GameLogicData.ParseGameLogicObjects))]
internal static class BetterBoPParsedRulesPatch
{
    [HarmonyPostfix]
    private static void ApplyParsedRules(GameLogicData __instance)
    {
        try
        {
            BetterBoPRules.Apply(__instance);
            BetterBoPRules.Logger.LogInfo("Applied Better BoP technology, diplomacy, embassy, Mind Bender, and Ai-Mo rules.");
        }
        catch (Exception exception)
        {
            BetterBoPRules.Logger.LogError($"Failed to apply parsed Better BoP rules: {exception}");
        }
    }
}

[HarmonyPatch(typeof(StartTurnAction), nameof(StartTurnAction.Execute))]
internal static class DiplomacyEmbassyIncomePatch
{
    [HarmonyPrefix]
    private static void UseDoubledIncomeForDiplomacy(
        StartTurnAction __instance,
        GameState state,
        out int __state
    )
    {
        __state = state.GameLogicData.DiplomacyData.embassyIncome;
        try
        {
            if (!state.TryGetPlayer(__instance.PlayerId, out PlayerState player)) return;
            if (!state.GameLogicData.IsUnlocked(TechData.Type.Diplomacy, player)) return;
            state.GameLogicData.DiplomacyData.embassyIncome = checked(__state * 2);
        }
        catch (Exception exception)
        {
            state.GameLogicData.DiplomacyData.embassyIncome = __state;
            BetterBoPRules.Logger.LogError($"Could not prepare doubled Diplomacy embassy income: {exception}");
        }
    }

    [HarmonyPostfix]
    private static void RestoreStrategyIncome(GameState state, int __state)
    {
        state.GameLogicData.DiplomacyData.embassyIncome = __state;
    }

    [HarmonyFinalizer]
    private static Exception? RestoreStrategyIncomeAfterFailure(
        Exception? __exception,
        GameState state,
        int __state
    )
    {
        try
        {
            state.GameLogicData.DiplomacyData.embassyIncome = __state;
        }
        catch (Exception restoreException)
        {
            BetterBoPRules.Logger.LogError($"Could not restore Strategy embassy income: {restoreException}");
        }
        return __exception;
    }
}

internal static class EmbassyActionButtonRegistry
{
    private static readonly Dictionary<IntPtr, Action> Actions = new();
    private static IntPtr lastExecutedButton;
    private static int lastExecutedFrame = -1;

    internal static void Clear()
    {
        Actions.Clear();
        lastExecutedButton = IntPtr.Zero;
        lastExecutedFrame = -1;
    }

    internal static void Register(UIRoundButton button, Action action)
    {
        Actions[button.Pointer] = action;
    }

    internal static bool TryExecute(UIButtonBase button)
    {
        if (button is not UIRoundButton roundButton ||
            !Actions.TryGetValue(roundButton.Pointer, out Action? action))
        {
            return false;
        }

        // Unity can route one physical click through both the pointer handler
        // and its backing Button event. Consume the second route in the same
        // frame without executing the command twice or falling back to vanilla.
        if (lastExecutedButton == roundButton.Pointer && lastExecutedFrame == Time.frameCount)
        {
            return true;
        }
        lastExecutedButton = roundButton.Pointer;
        lastExecutedFrame = Time.frameCount;
        action();
        return true;
    }
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.UpdateDiplomacyActionButtons))]
internal static class EmbassyActionButtonRegistryResetPatch
{
    [HarmonyPrefix]
    private static void ResetEmbassyButtons()
    {
        EmbassyActionButtonRegistry.Clear();
    }
}

[HarmonyPatch(typeof(UIButtonBase), nameof(UIButtonBase.OnPointerClick))]
internal static class EmbassyActionPointerClickPatch
{
    [HarmonyPrefix]
    private static bool InterceptEmbassyPointerClick(UIButtonBase __instance)
    {
        return !EmbassyActionButtonRegistry.TryExecute(__instance);
    }
}

[HarmonyPatch(typeof(UIButtonBase), nameof(UIButtonBase.OnButtonClicked))]
internal static class EmbassyActionButtonClickPatch
{
    [HarmonyPrefix]
    private static bool InterceptEmbassyButtonClick(UIButtonBase __instance)
    {
        return !EmbassyActionButtonRegistry.TryExecute(__instance);
    }
}

[HarmonyPatch]
internal static class AlwaysAvailablePeacePatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(GameLogicData),
        nameof(GameLogicData.IsUnlocked),
        new[] { typeof(PlayerAbility.Type), typeof(PlayerState) }
    );

    [HarmonyPostfix]
    private static void MakePeaceAvailable(PlayerAbility.Type abilityType, ref bool __result)
    {
        if (abilityType == PlayerAbility.Type.PeaceTreaty) __result = true;
    }
}

[HarmonyPatch]
internal static class AlwaysAvailablePeaceHasAbilityPatch
{
    private static MethodBase TargetMethod() => AccessTools.Method(
        typeof(GameLogicData),
        nameof(GameLogicData.HasAbility),
        new[] { typeof(PlayerState), typeof(PlayerAbility.Type) }
    );

    [HarmonyPostfix]
    private static void GiveEveryTribePeace(PlayerAbility.Type ability, ref bool __result)
    {
        if (ability == PlayerAbility.Type.PeaceTreaty) __result = true;
    }
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.CreateDiplomacyActionButton))]
internal static class DiplomacyActionButtonRulesPatch
{
    [HarmonyPostfix]
    private static void CorrectDiplomacyActionButton(
        PlayerInfoPopup __instance,
        CommandBase command,
        GameState gameState,
        UIRoundButton __result
    )
    {
        bool isPeace = command is PeaceTreatyCommand;
        bool isEmbassy = command is EstablishEmbassyCommand;
        if (!isPeace && !isEmbassy) return;

        PlayerState local = GameManager.LocalPlayer;
        bool available = isPeace || gameState.GameLogicData.IsUnlocked(TechData.Type.Shields, local);
        // The vanilla control wires separate enabled and disabled callbacks
        // that both retain Diplomacy as the requirement. Clear every callback,
        // not only the signal, or one click can establish the embassy and then
        // display the old Diplomacy warning afterward.
        __result.ClearCallbacks();
        __result.ButtonEnabled = true;
        __result.BlockButton = false;
        __result.buttonActive = available;
        Action action = () =>
        {
            if (available)
            {
                __instance.DiplomacyButton_OnClicked(command);
            }
            else
            {
                __instance.OnUnavailableDiplomacyCommandClicked(
                    command,
                    gameState.GameLogicData.GetTechData(TechData.Type.Shields)
                );
            }
        };
        EmbassyActionButtonRegistry.Register(__result, action);
        // Retain a signal fallback for non-pointer input. Registered pointer and
        // button clicks are intercepted before vanilla can also run its stale
        // Diplomacy callback.
        __result.OnClickedSignal.Add(
            DelegateSupport.ConvertDelegate<Il2CppSystem.Action>(() => action())
        );
    }
}

[HarmonyPatch(typeof(PlayerInfoPopup), nameof(PlayerInfoPopup.OnUnavailableDiplomacyCommandClicked))]
internal static class EmbassyUnavailableRequirementPatch
{
    [HarmonyPrefix]
    private static bool UseStrategyForEmbassies(CommandBase command, ref TechData techData)
    {
        if (command is not EstablishEmbassyCommand)
        {
            return true;
        }

        GameState state = GameManager.GameState;
        techData = state.GameLogicData.GetTechData(TechData.Type.Shields);

        // If Strategy is already researched, an obsolete vanilla callback must
        // not show any unavailable popup. Otherwise the original popup may run,
        // now displaying and linking to Strategy instead of Diplomacy.
        return !state.GameLogicData.IsUnlocked(TechData.Type.Shields, GameManager.LocalPlayer);
    }
}

[HarmonyPatch(typeof(PlayerAbilityExtensions), nameof(PlayerAbilityExtensions.GetDescription))]
internal static class EmbassyDescriptionPatch
{
    [HarmonyPostfix]
    private static void UseExactEmbassyIncome(PlayerAbility.Type __0, ref string __result)
    {
        if (__0 == PlayerAbility.Type.Embassy)
        {
            __result = "Establish an embassy in another tribe's capital. It gives both tribes 1 star per turn, or 2 stars per turn while they have a peace treaty. After researching Diplomacy, those amounts double to 2 and 4 stars per turn.";
        }
    }
}

[HarmonyPatch(typeof(TechPopupContent), nameof(TechPopupContent.CreateTechDataContent))]
internal static class BetterBoPTechPopupPatch
{
    [HarmonyPostfix]
    private static void AddBetterBoPTechInformation(TechData data, UIBasicComponent __result)
    {
        if (__result == null) return;
        try
        {
            if (data.type == TechData.Type.Shields)
            {
                AddInfo(
                    __result.rectTransform,
                    SpriteRef.UI_STARICON,
                    "Gift Stars",
                    "Choose another tribe and send 5, 10, or 20 stars. The receiving tribe gets 80% of the amount sent. Star gifts to bots create the Generous boon."
                );
            }
            else if (data.type == TechData.Type.Diplomacy)
            {
                AddInfo(
                    __result.rectTransform,
                    SpriteRef.UI_EMBASSY,
                    "Embassy Income Doubled",
                    "Researching Diplomacy doubles all current and future embassy income. Embassies give both tribes 2 stars per turn, or 4 stars per turn while they have a peace treaty."
                );
            }
        }
        catch (Exception exception)
        {
            BetterBoPRules.Logger.LogError($"Could not add Better BoP tech-panel icon for {data.type}: {exception}");
        }
    }

    private static void AddInfo(RectTransform parent, int sprite, string header, string description)
    {
        // Use the exact container and size used by the native unlock boons.
        // The old custom Large button was parented to the content root, which
        // forced it into the centre and made it cover the surrounding boons.
        var existingButtons = parent.GetComponentsInChildren<UIRoundButton_UI2>(true);
        RectTransform boonParent = parent;
        if (existingButtons.Length > 0)
        {
            RectTransform? nativeParent = existingButtons[0].rectTransform.parent.TryCast<RectTransform>();
            if (nativeParent != null) boonParent = nativeParent;
        }

        UIRoundButton_UI2 button = UILibrary.NewRoundButton(boonParent)
            .SetStyle(UIButtonBase_UI2.ButtonStyle.Default)
            .SetButtonSize(UIRoundButton_UI2.ButtonSize.Regular)
            .SetSprite(sprite, 0.58f);
        button.Text = header;
        button.ClearCallbacks();
        TechPopupContent.AddInfoIcon(button);
        TechPopupContent.AddInfoPopup(button, header, description);
        button.UpdateLabelVisibility();
        button.RunLayout();
        button.rectTransform.SetAsLastSibling();
    }
}

[HarmonyPatch(typeof(TechItem), nameof(TechItem.GetUnlockItems))]
internal static class BetterBoPTechTreeUnlockIconPatch
{
    [HarmonyPostfix]
    private static void AddVisibleTechTreeBoon(
        TechData techData,
        ref Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<RectTransform> __result
    )
    {
        int sprite = techData.type switch
        {
            TechData.Type.Shields => SpriteRef.UI_STARICON,
            TechData.Type.Diplomacy => SpriteRef.UI_EMBASSY,
            _ => -1,
        };
        if (sprite < 0 || __result == null || __result.Length == 0) return;

        try
        {
            // Clone an unlock icon already produced by Polytopia. This preserves
            // its exact node scale, outline, spacing, and layout behaviour while
            // changing only the displayed sprite.
            RectTransform template = __result[__result.Length - 1];
            RectTransform clone = UnityEngine.Object.Instantiate(template, template.parent);
            UnityEngine.Sprite boonSprite = GameManager.GetSpriteAtlasManager().GetSprite(sprite);
            UnityEngine.UI.Image? icon = clone.GetComponent<UnityEngine.UI.Image>();
            if (icon == null)
            {
                foreach (UnityEngine.UI.Image candidate in clone.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                {
                    if (candidate.sprite == null) continue;
                    icon = candidate;
                    break;
                }
            }
            if (icon != null)
            {
                icon.sprite = boonSprite;
                icon.preserveAspect = true;
                icon.color = Color.white;
            }

            var expanded = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<RectTransform>(
                __result.Length + 1
            );
            for (int index = 0; index < __result.Length; index++) expanded[index] = __result[index];
            expanded[__result.Length] = clone;
            __result = expanded;
        }
        catch (Exception exception)
        {
            BetterBoPRules.Logger.LogError($"Could not add the {techData.type} tech-tree boon icon: {exception}");
        }
    }
}

[HarmonyPatch(typeof(TechUtils), nameof(TechUtils.GetInfo))]
internal static class BetterBoPTechInfoTextPatch
{
    [HarmonyPostfix]
    private static void AddRuleText(TechData __0, ref string __result)
    {
        string addition = __0.type switch
        {
            TechData.Type.Shields => "Gift Stars: select another tribe to send 5, 10, or 20 stars. The receiving tribe gets 80%.",
            TechData.Type.Diplomacy => "Embassy Income Doubled: all current and future embassies give 2 stars per turn, or 4 during peace.",
            _ => string.Empty,
        };
        if (string.IsNullOrEmpty(addition) || (__result?.Contains(addition, StringComparison.Ordinal) ?? false)) return;
        __result = string.IsNullOrWhiteSpace(__result) ? addition : $"{__result}\n\n{addition}";
    }
}

[HarmonyPatch(typeof(TribeData), "get_description")]
internal static class AimoDescriptionPatch
{
    [HarmonyPostfix]
    private static void DescribeAimoStart(TribeData __instance, ref string __result)
    {
        if (__instance.type != TribeType.Aimo) return;

        __result = (__result ?? string.Empty)
            .Replace("Philosophy", "Meditation", StringComparison.OrdinalIgnoreCase)
            .Replace("Warrior", "Mind Bender", StringComparison.OrdinalIgnoreCase);
        if (!__result.Contains("Mind Bender", StringComparison.OrdinalIgnoreCase) ||
            !__result.Contains("Meditation", StringComparison.OrdinalIgnoreCase))
        {
            __result = $"{__result.Trim()}\n\nStarts with Meditation and a Mind Bender.";
        }
    }
}
