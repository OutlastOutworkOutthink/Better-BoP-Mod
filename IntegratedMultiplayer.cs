using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using PolytopiaBackendBase.Game;
using System.Collections;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine;

namespace BetterBoPMod;

internal static class IntegratedMultiplayer
{
    private const string ServerBaseUrl = "https://better-bop-server-production.up.railway.app";
    private const string ServerTokenKey = "betterbop.server.token.0.4.5";
    private const string RulesetId = "better-bop-0.4.5";
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim CheckLock = new(1, 1);
    private static readonly SemaphoreSlim CommandSubmitLock = new(1, 1);
    private static ManualLogSource logger = null!;
    private static CancellationTokenSource? polling;
    private static string activeGameId = string.Empty;
    private static string activeMatchId = string.Empty;
    private static int nextCommandIndex;
    private static bool active;

    internal static bool Active => active;
    internal static string ActiveGameId => activeGameId;

    internal static void Initialize(ManualLogSource logSource)
    {
        logger = logSource;
        Wake();
    }

    internal static void Wake()
    {
        polling?.Cancel();
        polling = new CancellationTokenSource();
        _ = PollLoopAsync(polling.Token);
    }

    private static async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(PlayerPrefs.GetString(DiscordIntegrationPatch.IntegrationTokenKey, string.Empty)))
                {
                    await CheckForAssignedGameAsync(false).ConfigureAwait(false);
                    if (active) await ReceiveCommandsAsync().ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                logger.LogWarning($"Integrated multiplayer poll failed: {exception.Message}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(active ? 3 : 15), cancellationToken).ConfigureAwait(false); }
            catch (TaskCanceledException) { return; }
        }
    }

    internal static async Task CheckForAssignedGameAsync(bool showStatus)
    {
        if (!await CheckLock.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            string token = await EnsureServerTokenAsync().ConfigureAwait(false);
            using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Get, "/v1/integrated-matches", token);
            using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Match service returned {(int)response.StatusCode}: {body}");
            MatchListResponse? list = JsonSerializer.Deserialize<MatchListResponse>(body);
            IntegratedMatch? match = list?.Matches.FirstOrDefault(item =>
                item.Status is "provisioning" or "active" or "awaiting_acceptance");
            if (match == null)
            {
                if (showStatus) DiscordIntegrationPatch.RunOnMainThread(() => DiscordIntegrationPatch.SetButtonText("No Games"));
                return;
            }
            if (match.Status == "awaiting_acceptance")
            {
                if (showStatus) DiscordIntegrationPatch.RunOnMainThread(() => DiscordIntegrationPatch.SetButtonText("Accept in Discord"));
                return;
            }
            activeMatchId = match.Id;
            activeGameId = match.GameId ?? string.Empty;
            nextCommandIndex = match.NextCommandIndex;
            if (string.IsNullOrWhiteSpace(activeGameId)) return;
            if (match.Role == "host" && match.Status == "provisioning")
            {
                await StartHostAsync(match, token).ConfigureAwait(false);
            }
            else if (match.Status == "active" && !active)
            {
                await ResumeParticipantAsync(match, token).ConfigureAwait(false);
            }
            if (showStatus) DiscordIntegrationPatch.RunOnMainThread(() => DiscordIntegrationPatch.SetButtonText(active ? "Game Active" : "Game Ready"));
        }
        finally
        {
            CheckLock.Release();
        }
    }

    private static async Task<string> EnsureServerTokenAsync()
    {
        string stored = PlayerPrefs.GetString(ServerTokenKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(stored)) return stored;
        string integrationToken = PlayerPrefs.GetString(DiscordIntegrationPatch.IntegrationTokenKey, string.Empty);
        if (string.IsNullOrWhiteSpace(integrationToken)) throw new InvalidOperationException("Connect Discord before opening Integrated games.");
        string json = JsonSerializer.Serialize(new
        {
            integrationToken,
            modVersion = DiscordIntegrationPatch.ModVersion,
            rulesetId = RulesetId,
        });
        using HttpResponseMessage response = await HttpClient.PostAsync(
            $"{ServerBaseUrl}/v1/auth/exchange",
            new StringContent(json, Encoding.UTF8, "application/json")
        ).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Server sign-in returned {(int)response.StatusCode}: {body}");
        AuthResponse? auth = JsonSerializer.Deserialize<AuthResponse>(body);
        if (string.IsNullOrWhiteSpace(auth?.Token)) throw new InvalidOperationException("Server sign-in returned no token.");
        PlayerPrefs.SetString(ServerTokenKey, auth.Token);
        PlayerPrefs.Save();
        return auth.Token;
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, string path, string token, string? json = null)
    {
        HttpRequestMessage request = new(method, $"{ServerBaseUrl}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task StartHostAsync(IntegratedMatch match, string token)
    {
        byte[] state = await RunOnMainThreadAsync(() =>
        {
            GameSettings settings = GameManager.PreliminaryGameSettings ?? BuildSettings(match);
            GameManager.Instance.SetLocalClient();
            MethodInfo create = typeof(ClientBase).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .First(method => method.Name == "CreateSession" && method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == typeof(GameSettings));
            create.Invoke(GameManager.Client, new object[] { settings, Il2CppSystem.Guid.Parse(match.GameId!) });
            GameManager.Instance.LoadLevel();
            active = true;
            return SerializeClient();
        }).ConfigureAwait(false);
        string payload = JsonSerializer.Serialize(new { serializedState = Convert.ToBase64String(state) });
        using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Put, $"/v1/games/{match.GameId}/initial-state", token, payload);
        using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Initial state upload returned {(int)response.StatusCode}: {body}");
        logger.LogMessage($"Hosted Integrated match {match.BotGameId} as server game {match.GameId}.");
    }

    private static async Task ResumeParticipantAsync(IntegratedMatch match, string token)
    {
        using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Get, $"/v1/games/{match.GameId}", token);
        using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Initial state download returned {(int)response.StatusCode}: {body}");
        GameResponse? game = JsonSerializer.Deserialize<GameResponse>(body);
        byte[] state = Convert.FromBase64String(game?.Game.InitialState ?? throw new InvalidOperationException("Host state is not ready."));
        await RunOnMainThreadAsync(() =>
        {
            GameManager.Instance.SetLocalClient();
            GameManager.Client.CreateSession(new Il2CppStructArray<byte>(state), Il2CppSystem.Guid.Parse(match.GameId!));
            GameManager.Instance.LoadLevel();
            // The server snapshot is the initial state. Replaying from command
            // zero restores every normal and Better BoP command after a restart.
            nextCommandIndex = 0;
            active = true;
            return true;
        }).ConfigureAwait(false);
        logger.LogMessage($"Loaded Integrated match {match.BotGameId} as {match.Role} and prepared ordered command replay.");
    }

    private static GameSettings BuildSettings(IntegratedMatch match)
    {
        GameSettings settings = new();
        settings.AddPlayer(BuildPlayer(match.HostAccountId, match.HostDisplayName));
        settings.AddPlayer(BuildPlayer(match.AwayAccountId, match.AwayDisplayName));
        return settings;
    }

    private static PlayerData BuildPlayer(string accountId, string name)
    {
        PlayerData player = new();
        PlayerProfileState profile = new();
        SetMember(profile, "id", Il2CppSystem.Guid.Parse(accountId));
        SetMember(profile, "name", name);
        SetMember(player, "profile", profile);
        SetMember(player, "defaultName", name);
        return player;
    }

    private static void SetMember(object target, string name, object value)
    {
        Type type = target.GetType();
        PropertyInfo? property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property?.CanWrite == true) { property.SetValue(target, value); return; }
        FieldInfo? field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        field?.SetValue(target, value);
    }

    private static byte[] SerializeClient()
    {
        Type wrapperType = FindType("ClientSerializationWrapper");
        object wrapper = Activator.CreateInstance(wrapperType, GameManager.Client)
            ?? throw new InvalidOperationException("Could not construct the game state serializer.");
        Type helperType = FindType("DiskSerializationHelpers");
        MethodInfo method = helperType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(item => item.Name == "ToLZ4CompressedByteArray" && item.IsGenericMethodDefinition && item.GetParameters().Length == 2)
            .MakeGenericMethod(wrapperType);
        object encoded = method.Invoke(null, new object[] { wrapper, GameManager.GameState.Version })
            ?? throw new InvalidOperationException("Game state serialization returned no data.");
        return Bytes(encoded);
    }

    internal static async Task SubmitCommandAsync(CommandBase command)
    {
        if (!active || string.IsNullOrWhiteSpace(activeGameId)) return;
        try
        {
            byte[] serialized = SerializeCommand(command);
            await PostSerializedCommandAsync(serialized, command is EndTurnCommand).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogError($"Integrated command upload failed: {exception}");
        }
    }

    internal static async Task SubmitGiftAsync(GiftStars.GiftEnvelope gift)
    {
        if (!active || string.IsNullOrWhiteSpace(activeGameId))
            throw new InvalidOperationException("Gift Stars multiplayer requires an active Integrated game.");

        byte[] serialized = GiftStars.Serialize(gift);
        await PostSerializedCommandAsync(serialized, false).ConfigureAwait(false);
        await RunOnMainThreadAsync(() =>
        {
            GiftStars.ApplyGift(GameManager.GameState, gift, true);
            return true;
        }).ConfigureAwait(false);
    }

    private static async Task PostSerializedCommandAsync(byte[] serialized, bool endsTurn)
    {
        await CommandSubmitLock.WaitAsync().ConfigureAwait(false);
        try
        {
            string token = await EnsureServerTokenAsync().ConfigureAwait(false);
            string payload = JsonSerializer.Serialize(new
            {
                commandIndex = nextCommandIndex,
                serializedData = Convert.ToBase64String(serialized),
                clientStateHash = (string?)null,
                endsTurn,
            });
            using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Post, $"/v1/games/{activeGameId}/commands", token, payload);
            using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Command upload returned {(int)response.StatusCode}: {body}");
            CommandResponse? result = JsonSerializer.Deserialize<CommandResponse>(body);
            if (result != null) nextCommandIndex = result.NextCommandIndex;
        }
        finally
        {
            CommandSubmitLock.Release();
        }
    }

    private static async Task ReceiveCommandsAsync()
    {
        string token = await EnsureServerTokenAsync().ConfigureAwait(false);
        using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Get, $"/v1/games/{activeGameId}/commands?after={nextCommandIndex - 1}", token);
        using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return;
        CommandListResponse? list = JsonSerializer.Deserialize<CommandListResponse>(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
        foreach (RemoteCommand remote in list?.Commands ?? Array.Empty<RemoteCommand>())
        {
            if (remote.CommandIndex < nextCommandIndex) continue;
            byte[] serialized = Convert.FromBase64String(remote.SerializedData);
            await RunOnMainThreadAsync(() =>
            {
                if (GiftStars.TryDeserialize(serialized, out GiftStars.GiftEnvelope? gift))
                {
                    GiftStars.ApplyGift(GameManager.GameState, gift!, false);
                }
                else
                {
                    CommandBase command = DeserializeCommand(serialized);
                    MethodInfo receive = AccessTools.Method(typeof(ClientBase), "ReceiveCommand", new[] { typeof(CommandBase) });
                    receive.Invoke(GameManager.Client, new object[] { command });
                }
                return true;
            }).ConfigureAwait(false);
            nextCommandIndex = remote.CommandIndex + 1;
        }
    }

    private static byte[] SerializeCommand(CommandBase command)
    {
        MethodInfo method = FindSerializationMethod("ToByteArray");
        return Bytes(method.Invoke(null, new object[] { command, GameManager.GameState.Version })!);
    }

    private static CommandBase DeserializeCommand(byte[] bytes)
    {
        MethodInfo method = FindSerializationMethod("FromByteArray");
        object?[] args = { new Il2CppStructArray<byte>(bytes), null, GameManager.GameState.Version };
        bool success = (bool)(method.Invoke(null, args) ?? false);
        if (!success || args[1] is not CommandBase command) throw new InvalidOperationException("Could not deserialize a remote command.");
        return command;
    }

    private static MethodInfo FindSerializationMethod(string name)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        foreach (Type type in assembly.GetTypes())
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            ParameterInfo[] parameters = method.GetParameters();
            if (method.Name == name && !method.IsGenericMethod &&
                ((name == "ToByteArray" && parameters.Length == 2 && parameters[0].ParameterType == typeof(CommandBase)) ||
                 (name == "FromByteArray" && parameters.Length == 3 && parameters[1].ParameterType.IsByRef && parameters[1].ParameterType.GetElementType() == typeof(CommandBase))))
                return method;
        }
        throw new MissingMethodException($"Could not find command {name}.");
    }

    private static Type FindType(string name) => AppDomain.CurrentDomain.GetAssemblies()
        .SelectMany(assembly => assembly.GetTypes()).First(type => type.Name == name);

    private static byte[] Bytes(object source)
    {
        if (source is byte[] bytes) return bytes;
        if (source is IEnumerable enumerable) return enumerable.Cast<object>().Select(Convert.ToByte).ToArray();
        throw new InvalidOperationException("Serialized data had an unexpected type.");
    }

    private static Task<T> RunOnMainThreadAsync<T>(Func<T> action)
    {
        TaskCompletionSource<T> source = new(TaskCreationOptions.RunContinuationsAsynchronously);
        DiscordIntegrationPatch.RunOnMainThread(() =>
        {
            try { source.SetResult(action()); }
            catch (Exception exception) { source.SetException(exception); }
        });
        return source.Task;
    }

    internal static async Task ReportResultAsync(string winnerAccountId)
    {
        if (!active || string.IsNullOrWhiteSpace(activeGameId) || string.IsNullOrWhiteSpace(winnerAccountId)) return;
        try
        {
            string token = await EnsureServerTokenAsync().ConfigureAwait(false);
            string payload = JsonSerializer.Serialize(new { winnerAccountId });
            using HttpRequestMessage request = AuthorizedRequest(HttpMethod.Post, $"/v1/games/{activeGameId}/result", token, payload);
            using HttpResponseMessage response = await HttpClient.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) logger.LogWarning($"Result report returned {(int)response.StatusCode}.");
        }
        catch (Exception exception) { logger.LogError($"Integrated result report failed: {exception}"); }
    }

    internal static string WinnerAccountId(byte winnerPlayerId)
    {
        MethodInfo? getPlayer = typeof(GameState).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(method => method.Name == "GetPlayer" && method.GetParameters().Length == 1);
        if (getPlayer == null) return string.Empty;
        Type parameterType = getPlayer.GetParameters()[0].ParameterType;
        object argument = Convert.ChangeType(winnerPlayerId, parameterType);
        object? player = getPlayer.Invoke(GameManager.GameState, new[] { argument });
        return FindAccountId(player, 0);
    }

    private static string FindAccountId(object? value, int depth)
    {
        if (value == null || depth > 3) return string.Empty;
        Type type = value.GetType();
        foreach (string idName in new[] { "accountId", "profileId", "userId" })
        {
            object? idValue = type.GetField(idName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value)
                ?? type.GetProperty(idName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);
            if (idValue != null && Il2CppSystem.Guid.TryParse(idValue.ToString(), out Il2CppSystem.Guid parsed) && parsed != Il2CppSystem.Guid.Empty)
                return parsed.ToString();
        }
        foreach (string nestedName in new[] { "profile", "playerData", "data", "user" })
        {
            object? nested = type.GetField(nestedName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value)
                ?? type.GetProperty(nestedName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(value);
            string found = FindAccountId(nested, depth + 1);
            if (!string.IsNullOrWhiteSpace(found)) return found;
        }
        return string.Empty;
    }

    private sealed class AuthResponse { [JsonPropertyName("token")] public string Token { get; init; } = string.Empty; }
    private sealed class MatchListResponse { [JsonPropertyName("matches")] public IntegratedMatch[] Matches { get; init; } = Array.Empty<IntegratedMatch>(); }
    private sealed class IntegratedMatch
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
        [JsonPropertyName("bot_game_id")] public string BotGameId { get; init; } = string.Empty;
        [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
        [JsonPropertyName("game_id")] public string? GameId { get; init; }
        [JsonPropertyName("role")] public string Role { get; init; } = string.Empty;
        [JsonPropertyName("next_command_index")] public int NextCommandIndex { get; init; }
        [JsonPropertyName("host_account_id")] public string HostAccountId { get; init; } = string.Empty;
        [JsonPropertyName("host_display_name")] public string HostDisplayName { get; init; } = string.Empty;
        [JsonPropertyName("away_account_id")] public string AwayAccountId { get; init; } = string.Empty;
        [JsonPropertyName("away_display_name")] public string AwayDisplayName { get; init; } = string.Empty;
    }
    private sealed class GameResponse { [JsonPropertyName("game")] public GamePayload Game { get; init; } = new(); }
    private sealed class GamePayload { [JsonPropertyName("initialState")] public string? InitialState { get; init; } }
    private sealed class CommandResponse { [JsonPropertyName("nextCommandIndex")] public int NextCommandIndex { get; init; } }
    private sealed class CommandListResponse { [JsonPropertyName("commands")] public RemoteCommand[] Commands { get; init; } = Array.Empty<RemoteCommand>(); }
    private sealed class RemoteCommand
    {
        [JsonPropertyName("command_index")] public int CommandIndex { get; init; }
        [JsonPropertyName("serialized_data")] public string SerializedData { get; init; } = string.Empty;
    }
}

[HarmonyPatch(typeof(ClientBase), "SendCommandRemote", new[] { typeof(CommandBase) })]
internal static class IntegratedCommandPatch
{
    [HarmonyPrefix]
    private static bool SendThroughBetterBoP(CommandBase __0)
    {
        if (!IntegratedMultiplayer.Active) return true;
        _ = IntegratedMultiplayer.SubmitCommandAsync(__0);
        return false;
    }
}

[HarmonyPatch(typeof(GameManager), nameof(GameManager.MatchEnded))]
internal static class IntegratedResultPatch
{
    [HarmonyPrefix]
    private static void ReportIntegratedWinner(byte __2)
    {
        if (!IntegratedMultiplayer.Active) return;
        string winnerAccountId = IntegratedMultiplayer.WinnerAccountId(__2);
        _ = IntegratedMultiplayer.ReportResultAsync(winnerAccountId);
    }
}
