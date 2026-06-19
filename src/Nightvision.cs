using System.Globalization;
using Clientprefs.API;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Memory.DynamicFunctions;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;

namespace Nightvision;

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
    private readonly HashSet<int> loadedPlayerCookies = [];
    private bool PersistenceAvailable => ClientprefsApi is not null;
    private bool ClientprefsReady => ClientprefsApi is not null && g_iCookieID != -1 && g_iCookieID2 != -1;

    private MemoryFunctionVoid<CCSPlayerPawn, CSPlayerState>? StateTransition;
    private readonly PluginState _state = new();
    private readonly Dictionary<int, CSPlayerState> _oldPlayerState = [];
    private readonly List<(CCheckTransmitInfo info, int slot)> _transmitObservers = [];
    private readonly List<int> _staleSlots = [];

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

        Logger.LogInformation(message, args);
    }

    public override void Load(bool hotReload)
    {
        TryHookStateTransition();

        RegisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull, HookMode.Post);
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect, HookMode.Post);
        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        AddCommand("nv", "Enable/disable nightvision", OnNightvisionCommand);
        AddCommand("nvi", "Change nightvision intensity", OnNightvisionIntensityCommand);

        if (hotReload)
            SyncConnectedPlayersFromRuntime();
    }

    public override void Unload(bool hotReload)
    {
        RemoveCommand("nv", OnNightvisionCommand);
        RemoveCommand("nvi", OnNightvisionIntensityCommand);
        RemoveListener<Listeners.CheckTransmit>(OnCheckTransmit);
        RemoveListener<Listeners.OnMapEnd>(OnMapEnd);
        RemoveListener<Listeners.OnMapStart>(OnMapStart);
        DeregisterEventHandler<EventPlayerConnectFull>(OnPlayerConnectFull, HookMode.Post);
        DeregisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect, HookMode.Post);

        TryUnhookStateTransition();
        UnsubscribeClientprefsEvents();
        Utils.RemoveAllPlayerPP(_state);

        _state.connectedSlots.Clear();
        _state.playerVars.Clear();
        loadedPlayerCookies.Clear();
        _oldPlayerState.Clear();

        ClientprefsApi = null;
        ClientprefsAvailabilityResolved = false;
        g_iCookieID = -1;
        g_iCookieID2 = -1;
    }

    private void TryHookStateTransition()
    {
        try
        {
            string signature = GameData.GetSignature("StateTransition");
            if (string.IsNullOrWhiteSpace(signature))
            {
                Logger.LogError("[Nightvision] StateTransition signature is stale and needs to be updated.");
                return;
            }

            StateTransition = new MemoryFunctionVoid<CCSPlayerPawn, CSPlayerState>(signature);
            StateTransition.Hook(Hook_StateTransition, HookMode.Post);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Nightvision] StateTransition signature is stale and needs to be updated.");
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

        if (hotReload)
            OnClientprefDatabaseReady();
    }

    private void UnsubscribeClientprefsEvents()
    {
        if (ClientprefsApi == null)
            return;

        ClientprefsApi.OnDatabaseLoaded -= OnClientprefDatabaseReady;
        ClientprefsApi.OnPlayerCookiesCached -= OnPlayerCookiesCached;
    }

    public async void OnClientprefDatabaseReady()
    {
        try
        {
            await RegisterClientprefCookiesAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Nightvision] Unhandled error while preparing player cookies.");
        }
    }

    private async Task RegisterClientprefCookiesAsync()
    {
        if (ClientprefsApi is null) return;

        int enabledCookieId;
        int intensityCookieId;
        try
        {
            enabledCookieId = await ClientprefsApi.RegPlayerCookieAsync("nightvision_enabled", "Nightvision status");
            intensityCookieId = await ClientprefsApi.RegPlayerCookieAsync("nightvision_intensity", "Nightvision intensity");
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Nightvision] Failed to register player cookies.");
            return;
        }

        Server.NextWorldUpdate(() =>
        {
            g_iCookieID = enabledCookieId;
            g_iCookieID2 = intensityCookieId;

            if (g_iCookieID == -1)
            {
                Logger.LogError("[Nightvision] Failed to register player cookie nightvision_enabled!");
                return;
            }

            if (g_iCookieID2 == -1)
            {
                Logger.LogError("[Nightvision] Failed to register player cookie nightvision_intensity!");
                return;
            }

            LogDebug("[Nightvision] Clientprefs ready. Player settings will now persist.");
            SyncConnectedPlayersFromPersistence();
        });
    }

    public void OnPlayerCookiesCached(CCSPlayerController player)
    {
        if (player == null || !player.IsValid || player.IsBot) return;
        if (!ClientprefsReady) return;
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

        _state.connectedSlots.Add(player.Slot);
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

    private void SyncConnectedPlayersFromRuntime()
    {
        foreach (CCSPlayerController player in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot))
            EnsurePlayerState(player);
    }

    private void SyncConnectedPlayersFromPersistence()
    {
        if (!ClientprefsReady) return;

        foreach (CCSPlayerController player in Utilities.GetPlayers().Where(p => p.IsValid && !p.IsBot))
        {
            EnsurePlayerState(player);

            if (!ClientprefsApi!.ArePlayerCookiesCached(player))
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
            _oldPlayerState.Remove(player.Slot);
            EnsurePlayerState(player);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        if (@event.Userid?.IsValid != true)
            return HookResult.Continue;

        var player = @event.Userid;
        if (player.IsValid && !player.IsBot)
        {
            Utils.OnPlayerDisconnect(_state, player);
            loadedPlayerCookies.Remove(player.Slot);
            _oldPlayerState.Remove(player.Slot);
        }

        return HookResult.Continue;
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

        _staleSlots.Clear();
        foreach (var (ownerSlot, pp) in _state.postProcessVolumes)
        {
            if (pp == null || !pp.IsValid)
                _staleSlots.Add(ownerSlot);
        }
        if (_staleSlots.Count > 0)
        {
            foreach (int slot in _staleSlots)
                _state.postProcessVolumes.Remove(slot);
            if (_state.postProcessVolumes.Count == 0)
                return;
        }

        _transmitObservers.Clear();
        foreach ((CCheckTransmitInfo info, CCSPlayerController? player) in infoList)
        {
            if (player == null || player.IsBot || !player.IsValid || player.IsHLTV)
                continue;

            if (!_state.connectedSlots.Contains(player.Slot))
                continue;

            _transmitObservers.Add((info, player.Slot));
        }

        if (_transmitObservers.Count == 0)
            return;

        foreach (var (ownerSlot, pp) in _state.postProcessVolumes)
        {
            foreach (var (info, slot) in _transmitObservers)
            {
                if (slot == ownerSlot)
                    continue;

                info.TransmitEntities.Remove(pp);
            }
        }
    }

    private void OnNightvisionCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || player.IsBot) return;
        if (player.Team == CsTeam.Spectator)
        {
            PrintPluginChat(player, $"{ChatColors.LightRed}Nightvision can't be used while spectating.");
            return;
        }
        if (!TryGetReadyPlayerVars(player, out var playerVars)) return;

        playerVars.NightvisionEnabled = !playerVars.NightvisionEnabled;

        if (playerVars.NightvisionEnabled)
            Utils.CreatePlayerPP(_state, player);
        else
            Utils.RemovePlayerPP(_state, player);

        PersistPlayerSettings(player, playerVars);
        PrintPluginChat(player, playerVars.NightvisionEnabled ? $"{ChatColors.Lime}Enabled" : $"{ChatColors.LightRed}Disabled");
    }

    private void OnNightvisionIntensityCommand(CCSPlayerController? player, CommandInfo info)
    {
        if (player == null || player.IsBot) return;
        if (player.Team == CsTeam.Spectator)
        {
            PrintPluginChat(player, $"{ChatColors.LightRed}Nightvision can't be used while spectating.");
            return;
        }
        if (!TryGetReadyPlayerVars(player, out var playerVars)) return;
        if (!playerVars.NightvisionEnabled) return;

        string arg = info.ArgByIndex(1);
        if (arg is null || arg == "")
        {
            PrintPluginChat(player, "Please provide a float value (E.g. !nvi 1.3)");
            return;
        }

        bool validIntensity = TryNormalizeNightvisionIntensity(arg, out float nvIntensity);
        if (!validIntensity)
        {
            nvIntensity = DefaultNightvisionIntensity;
            PrintPluginChat(player, $"Inputs are limited to between {MinNightvisionIntensity.ToString(CultureInfo.InvariantCulture)} and {MaxNightvisionIntensity.ToString(CultureInfo.InvariantCulture)}. Using {DefaultNightvisionIntensity.ToString(CultureInfo.InvariantCulture)}.");
        }

        playerVars.NightvisionIntensity = nvIntensity;
        PersistPlayerSettings(player, playerVars);

        Utils.RemovePlayerPP(_state, player);
        Utils.CreatePlayerPP(_state, player);

        PrintPluginChat(player, $"Intensity set to {ChatColors.Lime}{playerVars.NightvisionIntensity}");
    }

    private HookResult Hook_StateTransition(DynamicHook h)
    {
        var pawn = h.GetParam<CCSPlayerPawn>(0);
        if (pawn == null || !pawn.IsValid) return HookResult.Continue;

        var player = pawn.OriginalController.Value;
        var state = h.GetParam<CSPlayerState>(1);

        if (player is null) return HookResult.Continue;

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
