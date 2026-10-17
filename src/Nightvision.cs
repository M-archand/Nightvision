using System.Globalization;
using Clientprefs.API;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace Nightvision;

[MinimumApiVersion(369)]
public class Nightvision : BasePlugin, IPluginConfig<NightvisionConfig>
{
    public override string ModuleName => "Nightvision";
    public override string ModuleVersion => $"2.0.0";
    public override string ModuleAuthor => "rcnoob, Marchand";

    public NightvisionConfig Config { get; set; } = new();

    private const float MinNightvisionIntensity = 1f;
    private const float MaxNightvisionIntensity = 10f;
    private const float DefaultNightvisionIntensity = 5.0f;
    private const string DefaultChatPrefix = "[NightVision]";
    private const string EnabledCookieName = "nightvision_enabled";
    private const string IntensityCookieName = "nightvision_intensity";
    private const string DefaultChatPrefixColor = "Lime";
    private static readonly IReadOnlyDictionary<string, char> ChatColorMap = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase)
    {
        ["Blue"] = ChatColors.Blue,
        ["BlueGrey"] = ChatColors.BlueGrey,
        ["DarkBlue"] = ChatColors.DarkBlue,
        ["DarkRed"] = ChatColors.DarkRed,
        ["Default"] = ChatColors.Default,
        ["Gold"] = ChatColors.Gold,
        ["Green"] = ChatColors.Green,
        ["Grey"] = ChatColors.Grey,
        ["LightBlue"] = ChatColors.LightBlue,
        ["LightPurple"] = ChatColors.LightPurple,
        ["LightRed"] = ChatColors.LightRed,
        ["LightYellow"] = ChatColors.LightYellow,
        ["Lime"] = ChatColors.Lime,
        ["Magenta"] = ChatColors.Magenta,
        ["Olive"] = ChatColors.Olive,
        ["Orange"] = ChatColors.Orange,
        ["Purple"] = ChatColors.Purple,
        ["Red"] = ChatColors.Red,
        ["Silver"] = ChatColors.Silver,
        ["White"] = ChatColors.White,
        ["Yellow"] = ChatColors.Yellow
    };

    private readonly PluginCapability<IClientprefsApi> g_PluginCapability = new("Clientprefs");
    private IClientprefsApi? ClientprefsApi;
    private int g_iCookieID = -1, g_iCookieID2 = -1;
    private bool ClientprefsAvailabilityResolved;
    private bool _clientprefDatabaseLoaded;
    private bool _cookieRegistrationInFlight;
    private bool _cookieRegistrationRerunRequested;
    private readonly HashSet<int> loadedPlayerCookies = [];
    private bool PersistenceAvailable => ClientprefsApi is not null;
    private bool ClientprefsReady => ClientprefsApi is not null && g_iCookieID != -1 && g_iCookieID2 != -1;

    private MemoryFunctionVoid<CCSPlayerPawn, CSPlayerState>? StateTransition;
    private readonly PluginState _state = new();
    private readonly Dictionary<int, CSPlayerState> _oldPlayerState = [];

    public void OnConfigParsed(NightvisionConfig config)
    {
        config.ChatPrefix = NormalizeChatPrefix(config.ChatPrefix);
        config.ChatPrefixColor = NormalizeChatPrefixColor(config.ChatPrefixColor);
        Config = config;
    }

    private void LogDebug(string message, params object?[] args)
    {
        if (!Config.EnableDebug)
            return;

        Logger.LogDebug(message, args);
    }

    public override void Load(bool hotReload)
    {
        TryHookStateTransition();

        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull, HookMode.Post);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);
        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        AddCommand("css_nv", "Enable/disable nightvision", OnNightvisionCommand);
        AddCommand("css_nvi", "Change nightvision intensity", OnNightvisionIntensityCommand);
    }

    public override void Unload(bool hotReload)
    {
        RemoveCommand("css_nv", OnNightvisionCommand);
        RemoveCommand("css_nvi", OnNightvisionIntensityCommand);
        RemoveListener<Listeners.CheckTransmit>(OnCheckTransmit);
        RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
        RemoveListener<Listeners.OnMapStart>(OnMapStart);
        DeregisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull, HookMode.Post);
        RemoveListener<Listeners.OnClientDisconnect>(OnClientDisconnect);

        TryUnhookStateTransition();
        UnsubscribeClientprefsEvents();
        Utils.RemoveAllPlayerPP(_state);

        _state.playerVars.Clear();
        loadedPlayerCookies.Clear();
        _oldPlayerState.Clear();

        ClientprefsApi = null;
        ClientprefsAvailabilityResolved = false;
        _clientprefDatabaseLoaded = false;
        _cookieRegistrationInFlight = false;
        _cookieRegistrationRerunRequested = false;
        g_iCookieID = -1;
        g_iCookieID2 = -1;
    }

    private void TryHookStateTransition()
    {
        const string degraded = "Nightvision will not auto-apply on spawn or clear on death until this is fixed.";

        string signature;
        try
        {
            signature = GameData.GetSignature("StateTransition");
        }
        catch (ArgumentException)
        {
            Logger.LogError("[Nightvision] gamedata key 'StateTransition' not found. Copy gamedata/nvgamedata.json to addons/counterstrikesharp/gamedata/. {Degraded}", degraded);
            return;
        }
        catch (InvalidOperationException ex)
        {
            Logger.LogError(ex, "[Nightvision] nvgamedata.json has no StateTransition signature for this platform. {Degraded}", degraded);
            return;
        }

        if (string.IsNullOrWhiteSpace(signature))
        {
            Logger.LogError("[Nightvision] nvgamedata.json has an empty StateTransition signature for this platform. {Degraded}", degraded);
            return;
        }

        try
        {
            var function = new MemoryFunctionVoid<CCSPlayerPawn, CSPlayerState>(signature);

            if (function.Handle == IntPtr.Zero)
            {
                Logger.LogError("[Nightvision] StateTransition signature is stale and needs to be updated for the current CS2 build. {Degraded}", degraded);
                return;
            }

            function.Hook(Hook_StateTransition, HookMode.Post);
            StateTransition = function;
        }
        catch (NativeException ex)
        {
            Logger.LogError(ex, "[Nightvision] StateTransition signature is stale or the hook was rejected by CounterStrikeSharp. {Degraded}", degraded);
            StateTransition = null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Nightvision] Failed to hook StateTransition. {Degraded}", degraded);
            StateTransition = null;
        }
    }

    private void TryUnhookStateTransition()
    {
        if (StateTransition == null)
            return;

        try
        {
            StateTransition.Unhook(Hook_StateTransition, HookMode.Post);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Nightvision] Failed to unhook StateTransition during unload.");
        }
        finally
        {
            StateTransition = null;
        }
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        ClientprefsApi = g_PluginCapability.Get();
        ClientprefsAvailabilityResolved = true;

        if (ClientprefsApi == null)
        {
            Logger.LogWarning("[Nightvision] Clientprefs not available. Nightvision settings will be session-only.");
            return;
        }

        LogDebug("[Nightvision] Clientprefs detected. Nightvision settings will persist between sessions.");

        ClientprefsApi.OnDatabaseLoaded += OnClientprefDatabaseReady;
        ClientprefsApi.OnPlayerCookiesCached += OnPlayerCookiesCached;

        if (CookiesAlreadyKnown(ClientprefsApi))
            _clientprefDatabaseLoaded = true;

        if (hotReload || _clientprefDatabaseLoaded)
            TryRegisterClientprefCookies();
    }

    private static bool CookiesAlreadyKnown(IClientprefsApi api)
    {
        return api.FindPlayerCookie(EnabledCookieName) != -1
            && api.FindPlayerCookie(IntensityCookieName) != -1;
    }

    private void UnsubscribeClientprefsEvents()
    {
        if (ClientprefsApi == null)
            return;

        ClientprefsApi.OnDatabaseLoaded -= OnClientprefDatabaseReady;
        ClientprefsApi.OnPlayerCookiesCached -= OnPlayerCookiesCached;
    }

    private void OnClientprefDatabaseReady()
    {
        _clientprefDatabaseLoaded = true;
        TryRegisterClientprefCookies();
    }

    // Single entry point for every trigger
    private void TryRegisterClientprefCookies()
    {
        var api = ClientprefsApi;
        if (api is null || ClientprefsReady) return;

        if (_cookieRegistrationInFlight)
        {
            _cookieRegistrationRerunRequested = true;
            return;
        }

        _cookieRegistrationInFlight = true;
        _ = RegisterClientprefCookiesAsync(api);
    }

    private async Task RegisterClientprefCookiesAsync(IClientprefsApi api)
    {
        int enabledCookieId = -1;
        int intensityCookieId = -1;
        Exception? error = null;

        try
        {
            enabledCookieId = await api.RegPlayerCookieAsync(EnabledCookieName, "Nightvision status", CookieAccess.CookieAccess_Private);
            intensityCookieId = await api.RegPlayerCookieAsync(IntensityCookieName, "Nightvision intensity", CookieAccess.CookieAccess_Private);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        Server.NextWorldUpdate(() => CompleteCookieRegistration(enabledCookieId, intensityCookieId, error));
    }

    private void CompleteCookieRegistration(int enabledCookieId, int intensityCookieId, Exception? error)
    {
        _cookieRegistrationInFlight = false;
        bool rerunRequested = _cookieRegistrationRerunRequested;
        _cookieRegistrationRerunRequested = false;

        if (ClientprefsApi is null) return;

        try
        {
            if (error is not null)
            {
                Logger.LogError(error, "[Nightvision] Failed to register player cookies.");
            }
            else if (enabledCookieId == -1 || intensityCookieId == -1)
            {
                // Clientprefs returns -1 while its database is not loaded yet. OnDatabaseLoaded
                // or the first OnPlayerCookiesCached triggers another attempt.
                if (_clientprefDatabaseLoaded && !rerunRequested)
                    Logger.LogError("[Nightvision] Failed to register player cookies ({EnabledCookie}={EnabledId}, {IntensityCookie}={IntensityId}).", EnabledCookieName, enabledCookieId, IntensityCookieName, intensityCookieId);
                else
                    LogDebug("[Nightvision] Clientprefs database not loaded yet. Waiting for OnDatabaseLoaded.");
            }
            else
            {
                g_iCookieID = enabledCookieId;
                g_iCookieID2 = intensityCookieId;

                LogDebug("[Nightvision] Clientprefs ready. Player settings will now persist.");
                SyncConnectedPlayersFromPersistence();
                return;
            }

            if (rerunRequested)
                TryRegisterClientprefCookies();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Nightvision] Failed to apply player cookies on world update.");
        }
    }

    public void OnPlayerCookiesCached(CCSPlayerController player)
    {
        if (player == null || !player.IsValid || player.IsBot) return;
        if (!ClientprefsReady)
        {
            // Cookies are only cached once the Clientprefs database is loaded
            _clientprefDatabaseLoaded = true;
            TryRegisterClientprefCookies();
            return;
        }
        if (loadedPlayerCookies.Contains(player.Slot)) return;

        var playerVars = EnsurePlayerState(player);

        string enabledCookie = ClientprefsApi!.GetPlayerCookie(player, g_iCookieID);
        string intensityCookie = ClientprefsApi.GetPlayerCookie(player, g_iCookieID2);
        loadedPlayerCookies.Add(player.Slot);

        if (TryNormalizeNightvisionIntensity(intensityCookie, out float nvIntensity))
        {
            playerVars.NightvisionIntensity = nvIntensity;
        }
        else
        {
            playerVars.NightvisionIntensity = DefaultNightvisionIntensity;
        }

        playerVars.NightvisionEnabled = string.Equals(enabledCookie, "true", StringComparison.OrdinalIgnoreCase);

        if (playerVars.NightvisionEnabled && player.PawnIsAlive)
            Utils.CreatePlayerPP(_state, player);
        else
            Utils.RemovePlayerPP(_state, player);
    }

    private PlayerVars EnsurePlayerState(CCSPlayerController player)
    {
        if (!_state.playerVars.TryGetValue(player.Slot, out var playerVars))
        {
            playerVars = new PlayerVars();
            _state.playerVars[player.Slot] = playerVars;
        }

        return playerVars;
    }

    private static string NormalizeChatPrefix(string? chatPrefix)
    {
        if (string.IsNullOrWhiteSpace(chatPrefix))
            return DefaultChatPrefix;

        return chatPrefix.Trim();
    }

    private static string NormalizeChatPrefixColor(string? chatPrefixColor)
    {
        if (string.IsNullOrWhiteSpace(chatPrefixColor))
            return DefaultChatPrefixColor;

        string normalizedColor = chatPrefixColor.Trim();
        return ChatColorMap.ContainsKey(normalizedColor) ? normalizedColor : DefaultChatPrefixColor;
    }

    private void PrintPluginChat(CCSPlayerController player, string message)
    {
        player.PrintToChat($"{ ChatColorMap[Config.ChatPrefixColor]}{Config.ChatPrefix}{ChatColors.Default} {message}");
    }

    private bool TryGetReadyPlayerVars(CCSPlayerController player, out PlayerVars playerVars)
    {
        playerVars = EnsurePlayerState(player);

        if (!ClientprefsAvailabilityResolved)
        {
            PrintPluginChat(player, "Settings are still loading. Try again in a moment.");
            return false;
        }

        if (!PersistenceAvailable)
            return true;

        if (!ClientprefsReady)
        {
            PrintPluginChat(player, "Persistent settings are still loading. Try again in a moment.");
            return false;
        }

        if (loadedPlayerCookies.Contains(player.Slot))
            return true;

        PrintPluginChat(player, "Persistent settings are still loading. Try again in a moment.");
        return false;
    }

    private void SyncConnectedPlayersFromPersistence()
    {
        if (!ClientprefsReady) return;

        foreach (CCSPlayerController player in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot))
        {
            EnsurePlayerState(player);

            if (ClientprefsApi!.ArePlayerCookiesCached(player))
                OnPlayerCookiesCached(player);
        }
    }

    private static bool TryNormalizeNightvisionIntensity(string? value, out float normalizedIntensity)
    {
        normalizedIntensity = DefaultNightvisionIntensity;

        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedIntensity))
            return false;

        if (parsedIntensity < MinNightvisionIntensity || parsedIntensity > MaxNightvisionIntensity)
            return false;

        normalizedIntensity = parsedIntensity;
        return true;
    }

    private void PersistPlayerSettings(CCSPlayerController player, PlayerVars playerVars)
    {
        if (!ClientprefsReady) return;

        ClientprefsApi!.SetPlayerCookie(player, g_iCookieID, playerVars.NightvisionEnabled ? "true" : "false");
        ClientprefsApi.SetPlayerCookie(player, g_iCookieID2, playerVars.NightvisionIntensity.ToString(CultureInfo.InvariantCulture));
    }

    private HookResult OnPlayerConnectFull(EventPlayerConnectFull @event, GameEventInfo info)
    {
        if (@event.Userid?.IsValid != true)
            return HookResult.Continue;

        var player = @event.Userid;
        if (player.IsValid && !player.IsBot)
        {
            OnClientDisconnect(player.Slot);
            EnsurePlayerState(player);
        }

        return HookResult.Continue;
    }

    private void OnClientDisconnect(int playerSlot)
    {
        Utils.RemovePlayerPP(_state, playerSlot);
        _state.playerVars.Remove(playerSlot);
        loadedPlayerCookies.Remove(playerSlot);
        _oldPlayerState.Remove(playerSlot);
    }


    private void OnMapEnd()
    {
        Utils.RemoveAllPlayerPP(_state);
        _oldPlayerState.Clear();
    }

    private void OnMapStart(string mapName)
    {
        Server.NextWorldUpdate(() =>
        {
            foreach (CCSPlayerController player in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot))
            {
                if (_state.playerVars.TryGetValue(player.Slot, out var playerVars))
                {
                    if (playerVars.NightvisionEnabled && player.PawnIsAlive)
                        Utils.CreatePlayerPP(_state, player);

                    continue;
                }

                if (ClientprefsReady && ClientprefsApi!.ArePlayerCookiesCached(player))
                    OnPlayerCookiesCached(player);
            }
        });
    }

    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (_state.postProcessVolumes.Count == 0)
            return;

        var transmitObservers = new List<(CCheckTransmitInfo info, int slot)>();
        foreach ((CCheckTransmitInfo info, CCSPlayerController? player) in infoList)
        {
            int slot = player != null && player.IsValid ? player.Slot : -1;
            transmitObservers.Add((info, slot));
        }

        if (transmitObservers.Count == 0)
            return;

        List<int>? staleSlots = null;
        foreach (var (ownerSlot, handle) in _state.postProcessVolumes)
        {
            if (EntitySystem.GetEntityByHandle(handle) is null)
            {
                (staleSlots ??= []).Add(ownerSlot);
                continue;
            }

            int volumeIndex = (int)handle.Index;
            foreach (var (info, slot) in transmitObservers)
            {
                if (slot == ownerSlot)
                    continue;

                info.TransmitEntities.Remove(volumeIndex);
            }
        }

        if (staleSlots is null)
            return;

        foreach (int slot in staleSlots)
            _state.postProcessVolumes.Remove(slot);
    }

    private void OnNightvisionCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || player.IsBot) return;
        if (player.Team == CsTeam.Spectator || player.Team == CsTeam.None)
        {
            PrintPluginChat(player, $"{ChatColors.LightRed}Nightvision can't be used while spectating.");
            return;
        }
        if (!TryGetReadyPlayerVars(player, out var playerVars)) return;

        playerVars.NightvisionEnabled = !playerVars.NightvisionEnabled;

        if (playerVars.NightvisionEnabled && player.PawnIsAlive)
            Utils.CreatePlayerPP(_state, player);
        else
            Utils.RemovePlayerPP(_state, player);

        PersistPlayerSettings(player, playerVars);
        PrintPluginChat(player, playerVars.NightvisionEnabled ? $"{ChatColors.Lime}Enabled" : $"{ChatColors.LightRed}Disabled");
    }

    private void OnNightvisionIntensityCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || player.IsBot) return;
        if (player.Team == CsTeam.Spectator || player.Team == CsTeam.None)
        {
            PrintPluginChat(player, $"{ChatColors.LightRed}Nightvision can't be used while spectating.");
            return;
        }
        if (!TryGetReadyPlayerVars(player, out var playerVars)) return;

        string arg = info.ArgByIndex(1);
        if (arg is null || arg == "")
        {
            PrintPluginChat(player, "Please provide a float value (E.g. !nvi 1.3)");
            return;
        }

        if (!TryNormalizeNightvisionIntensity(arg, out float nvIntensity))
        {
            PrintPluginChat(player, $"Inputs are limited to between {MinNightvisionIntensity.ToString(CultureInfo.InvariantCulture)} and {MaxNightvisionIntensity.ToString(CultureInfo.InvariantCulture)}. Keeping {playerVars.NightvisionIntensity.ToString(CultureInfo.InvariantCulture)}.");
            return;
        }

        playerVars.NightvisionIntensity = nvIntensity;
        PersistPlayerSettings(player, playerVars);

        if (playerVars.NightvisionEnabled && player.PawnIsAlive)
            Utils.CreatePlayerPP(_state, player);
        else
            Utils.RemovePlayerPP(_state, player);

        string intensityText = playerVars.NightvisionIntensity.ToString(CultureInfo.InvariantCulture);
        PrintPluginChat(player, playerVars.NightvisionEnabled
            ? $"Intensity set to {ChatColors.Lime}{intensityText}"
            : $"Intensity set to {ChatColors.Lime}{intensityText}{ChatColors.Default}. Type !nv to enable.");
    }

    private HookResult Hook_StateTransition(DynamicHook h)
    {
        var pawn = h.GetParam<CCSPlayerPawn>(0);
        if (pawn == null || !pawn.IsValid) return HookResult.Continue;

        var player = pawn.OriginalController?.Value;
        var state = h.GetParam<CSPlayerState>(1);

        if (player is null || !player.IsValid) return HookResult.Continue;

        _oldPlayerState.TryGetValue(player.Slot, out var oldState);

        if (state != oldState)
        {
            bool becameActive = state == CSPlayerState.STATE_ACTIVE;
            bool leftActive = oldState == CSPlayerState.STATE_ACTIVE;

            if (leftActive && !becameActive)
            {
                Utils.RemovePlayerPP(_state, player);
            }
            else if (becameActive && !leftActive)
            {
                if (_state.playerVars.TryGetValue(player.Slot, out var playerVars) && playerVars.NightvisionEnabled)
                    Utils.CreatePlayerPP(_state, player);
            }
        }

        _oldPlayerState[player.Slot] = state;

        return HookResult.Continue;
    }
}
