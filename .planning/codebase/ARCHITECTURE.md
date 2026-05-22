<!-- refreshed: 2026-05-04 -->
# Architecture

**Analysis Date:** 2026-05-04

## System Overview

```text
┌────────────────────────────────────────────────────────────────────┐
│              CounterStrikeSharp Plugin Host (CSS)                  │
│         (BasePlugin lifecycle, event bus, entity registry)         │
└──────────────────────────┬─────────────────────────────────────────┘
                           │
                           ▼
┌────────────────────────────────────────────────────────────────────┐
│                   Nightvision (plugin root)                        │
│                   `Nightvision/Nightvision.cs`                     │
│   Load/Unload  │  Event handlers  │  Commands  │  State machine    │
└──────┬─────────┴──────────┬────────┴────────────┴──────────────────┘
       │                    │                         │
       ▼                    ▼                         ▼
┌─────────────┐   ┌──────────────────┐   ┌──────────────────────────┐
│  Globals.cs │   │    Utils.cs      │   │   ForceFullUpdate.cs     │
│ (slot-keyed │   │ (PP volume CRUD, │   │ (INetworkServerService,  │
│  state maps)│   │  player connect/ │   │  INetworkGameServer,     │
│             │   │  disconnect)     │   │  CServerSideClient,      │
└─────────────┘   └──────────────────┘   │  CUtlVector/CUtlMemory)  │
       ▲                  ▲              └──────────────────────────┘
       │                  │
       └──────────────────┘
            shared via Globals

       │  (optional capability)
       ▼
┌────────────────────────────────────────────────────────────────────┐
│              External: Clientprefs plugin                          │
│          IClientprefsApi  `ClientprefsApi/IClientprefs.cs`         │
│   OnDatabaseLoaded → cookie registration                           │
│   OnPlayerCookiesCached → per-player settings restore             │
└────────────────────────────────────────────────────────────────────┘
       │
       ▼
┌────────────────────────────────────────────────────────────────────┐
│          CS2 Game Entity: CPostProcessingVolume                    │
│    one entity per player with NV on, keyed by player.Slot          │
│    visibility restricted per-recipient via CheckTransmit           │
└────────────────────────────────────────────────────────────────────┘
```

## Component Responsibilities

| Component | Responsibility | File |
|-----------|----------------|------|
| `Nightvision` (plugin class) | Plugin lifecycle, event/command wiring, persistence orchestration, observer state machine | `Nightvision/Nightvision.cs` |
| `Globals` | Shared slot-keyed static maps: connected players, PP volume handles, player vars | `Nightvision/Globals.cs` |
| `Utils` | `CPostProcessingVolume` entity CRUD; player connect/disconnect dictionary bookkeeping | `Nightvision/Utils.cs` |
| `PlayerVars` | Per-player runtime state: `NightvisionEnabled` (bool), `NightvisionIntensity` (float) | `Nightvision/Classes.cs` |
| `NightvisionConfig` | Deserialized plugin config: `EnableDebug`, `ChatPrefix`, `ChatPrefixColor` | `Nightvision/NightvisionConfig.cs` |
| `INetworkServerService` | Valve interface wrapper — obtains `INetworkGameServer` via vtable offset | `Nightvision/ForceFullUpdate.cs:31` |
| `INetworkGameServer` | Reads native `CUtlVector` Slots array from memory to locate per-client handles | `Nightvision/ForceFullUpdate.cs:45` |
| `CServerSideClient` | Writes `m_nForceWaitForTick = -1` to trigger a forced full network update | `Nightvision/ForceFullUpdate.cs:65` |
| `CUtlVector` / `CUtlMemory` | Blittable structs mirroring Source 2 native memory layout for slot iteration | `Nightvision/ForceFullUpdate.cs:8,16` |
| `IClientprefsApi` | Interface contract for the optional Clientprefs persistence plugin | `ClientprefsApi/IClientprefs.cs:69` |

## Pattern Overview

**Overall:** Event-driven CounterStrikeSharp plugin with slot-keyed per-player state, per-player `CPostProcessingVolume` entity management, and optional persistence via a capability-discovered external plugin.

**Key Characteristics:**
- All per-player state is keyed by `player.Slot` (int), not by a player object reference, because CSS entity handles can be invalidated across ticks.
- The nightvision effect is produced by spawning a `CPostProcessingVolume` entity per player and setting `MaxExposure`/`MinExposure` to the desired intensity — not a shader, HUD overlay, or client-side effect.
- PP volume visibility is filtered on a per-recipient basis in `CheckTransmit`: each player only receives their own PP volume, preventing NV from affecting spectators or other players.
- Clientprefs persistence is optional. The plugin degrades gracefully to session-only behaviour when the Clientprefs plugin is absent or its database is not yet ready.

## Layers

**Plugin Orchestration Layer:**
- Purpose: Wire CSS lifecycle hooks, register events and commands, orchestrate all other components
- Location: `Nightvision/Nightvision.cs`
- Contains: `Load`, `Unload`, `OnAllPluginsLoaded`, event handlers, command handlers, Clientprefs callbacks, state-transition hook, ForceFullUpdate helper
- Depends on: `Globals`, `Utils`, ForceFullUpdate types, `IClientprefsApi`, `NightvisionConfig`
- Used by: CSS plugin host

**Shared State Layer:**
- Purpose: Single source of truth for runtime player state; accessed by both `Nightvision.cs` and `Utils.cs`
- Location: `Nightvision/Globals.cs`
- Contains: Three static `Dictionary<int, T>` fields: `connectedPlayers`, `postProcessVolumes`, `playerVars`
- Depends on: CSS entity types (`CCSPlayerController`, `CPostProcessingVolume`)
- Used by: `Nightvision/Nightvision.cs`, `Nightvision/Utils.cs`

**Effect Management Layer:**
- Purpose: Manages `CPostProcessingVolume` entity lifecycle (create, remove, remove-all) and player slot dictionary bookkeeping
- Location: `Nightvision/Utils.cs`
- Contains: `CreatePlayerPP`, `RemovePlayerPP`, `RemoveAllPlayerPP`, `OnPlayerConnect`, `OnPlayerDisconnect`
- Depends on: `Globals`, CSS `Utilities.CreateEntityByName<CPostProcessingVolume>`
- Used by: `Nightvision/Nightvision.cs`

**Native Interop Layer:**
- Purpose: Triggers a forced full network update when a player enters/exits observer mode so the corrected PP entity set is transmitted immediately
- Location: `Nightvision/ForceFullUpdate.cs`
- Contains: `INetworkServerService`, `INetworkGameServer`, `CServerSideClient`, `CUtlVector`, `CUtlMemory`
- Depends on: `CounterStrikeSharp.API.Modules.Memory`, `GameData.GetOffset(...)`, unsafe pointer arithmetic, `nvgamedata.json` signatures/offsets
- Used by: `Nightvision/Nightvision.cs:ForceFullUpdate()`

**Persistence Contract Layer:**
- Purpose: Defines the interface for cookie-based per-player preference persistence across sessions
- Location: `ClientprefsApi/IClientprefs.cs`
- Contains: `IClientprefsApi` interface, `CookieAccess` / `CookieMenu` / `CookieMenuAction` enums
- Depends on: CSS core types only
- Used by: `Nightvision/Nightvision.cs` via `PluginCapability<IClientprefsApi>`

**Configuration Layer:**
- Purpose: Typed deserialization of the JSON plugin config provided by CSS
- Location: `Nightvision/NightvisionConfig.cs`
- Contains: `NightvisionConfig : BasePluginConfig` with `EnableDebug`, `ChatPrefix`, `ChatPrefixColor`
- Depends on: `CounterStrikeSharp.API.Core`
- Used by: `Nightvision/Nightvision.cs` via `IPluginConfig<NightvisionConfig>`

## Data Flow

### Plugin Startup

1. CSS host calls `OnConfigParsed` — config is normalized and stored (`Nightvision/Nightvision.cs:63`)
2. CSS host calls `Load(hotReload)` — `TryHookStateTransition` installs memory function hook; events and commands registered (`Nightvision/Nightvision.cs:78`)
3. If hot reload: `SyncConnectedPlayersFromRuntime` populates `Globals.connectedPlayers` and `Globals.playerVars` for already-connected players (`Nightvision/Nightvision.cs:91`)
4. CSS host calls `OnAllPluginsLoaded` — `PluginCapability<IClientprefsApi>.Get()` resolves optional Clientprefs plugin (`Nightvision/Nightvision.cs:164`)
5. If Clientprefs present: subscribe to `OnDatabaseLoaded` and `OnPlayerCookiesCached` (`Nightvision/Nightvision.cs:177-178`)
6. If Clientprefs present and hot reload: call `OnClientprefDatabaseReady` immediately to re-register cookies (`Nightvision/Nightvision.cs:181`)

### Clientprefs Ready Path

1. `OnClientprefDatabaseReady` fires (from Clientprefs plugin event) (`Nightvision/Nightvision.cs:193`)
2. Two cookies registered: `nightvision_enabled` → `g_iCookieID`; `nightvision_intensity` → `g_iCookieID2` (`Nightvision/Nightvision.cs:197-198`)
3. `SyncConnectedPlayersFromPersistence` iterates connected players and calls `TryLoadPlayerCookies` for each (`Nightvision/Nightvision.cs:325`)

### Player Connect Path

1. `EventPlayerConnectFull` fires → `OnPlayerConnectFull` (`Nightvision/Nightvision.cs:358`)
2. `Utils.OnPlayerConnect` initialises a default `PlayerVars` and stores the player handle in `Globals.connectedPlayers` and `Globals.playerVars` (`Nightvision/Utils.cs:9`)
3. `TryLoadPlayerCookies` checks `ArePlayerCookiesCached`; if cached, calls `OnPlayerCookiesCached` immediately (`Nightvision/Nightvision.cs:307`)
4. `OnPlayerCookiesCached`: reads `nightvision_enabled` and `nightvision_intensity` cookies, updates `PlayerVars`, and calls `Utils.CreatePlayerPP` if NV was enabled (`Nightvision/Nightvision.cs:216`)

### Nightvision Toggle (!nv command)

1. Player issues `!nv` → `OnNightvisionCommand` (`Nightvision/Nightvision.cs:411`)
2. `TryGetReadyPlayerVars` checks `ClientprefsAvailabilityResolved`, `ClientprefsReady`, `loadedPlayerCookies`; returns false with a chat message if still loading (`Nightvision/Nightvision.cs:278`)
3. `playerVars.NightvisionEnabled` toggled
4. `Utils.CreatePlayerPP` or `Utils.RemovePlayerPP` called accordingly (`Nightvision/Nightvision.cs:419-421`)
5. `PersistPlayerSettings` writes both cookies (no-op if Clientprefs unavailable) (`Nightvision/Nightvision.cs:350`)

### PP Entity Create (`Utils.CreatePlayerPP`)

1. `RemovePlayerPP` called first to prevent duplicate entities (`Nightvision/Utils.cs:39`)
2. `CPostProcessingVolume` created via `Utilities.CreateEntityByName<CPostProcessingVolume>` (`Nightvision/Utils.cs:41`)
3. `pp.Master = true`, `pp.FadeDuration = 0f`, `pp.ExposureControl = true` set (`Nightvision/Utils.cs:45-48`)
4. `pp.MaxExposure = pp.MinExposure = playerVars.NightvisionIntensity` — exposure clamping at intensity value brightens the scene to simulate NV (`Nightvision/Utils.cs:49-50`)
5. `pp.DispatchSpawn()` — entity enters the game world (`Nightvision/Utils.cs:52`)
6. Handle stored in `Globals.postProcessVolumes[playerSlot]` (`Nightvision/Utils.cs:54`)

### CheckTransmit (PP Entity Visibility Filtering)

1. Every network transmit cycle `OnCheckTransmit` fires with the full transmit info list (`Nightvision/Nightvision.cs:388`)
2. Skip if `Globals.postProcessVolumes` is empty (fast path) (`Nightvision/Nightvision.cs:390`)
3. For each valid, non-bot, non-HLTV connected player iterate all PP volumes
4. Remove from `info.TransmitEntities` any PP volume whose `ownerSlot != player.Slot` (`Nightvision/Nightvision.cs:406`)
5. Result: each player receives only their own PP entity; no cross-player NV leakage

### Observer State Change Path

1. `Hook_StateTransition` fires post-call when `CCSPlayerPawn::StateTransition` runs (`Nightvision/Nightvision.cs:456`)
2. New state compared against `_oldPlayerState[player.Index]` (65-element `CSPlayerState[]` indexed by pawn entity index, not slot)
3. On entering or exiting `STATE_OBSERVER_MODE`: `ForceFullUpdate(player)` invoked (`Nightvision/Nightvision.cs:466`)
4. `ForceFullUpdate`: obtains `INetworkGameServer` via `INetworkServerService.GetIGameServer()`, calls `GetClientBySlot(player.Slot)?.ForceFullUpdate()` which writes `m_nForceWaitForTick = -1` (`Nightvision/Nightvision.cs:479-482`)
5. `player.PlayerPawn.Value?.Teleport(null, eyeAngles, null)` additionally forces a pawn update (`Nightvision/Nightvision.cs:482`)
6. Reason: entering/leaving observer mode stales the transmitted PP entity set; forced update re-sends the correct set

### Player Disconnect

1. `EventPlayerDisconnect` fires → `OnPlayerDisconnect` (`Nightvision/Nightvision.cs:373`)
2. `Utils.OnPlayerDisconnect`: `RemovePlayerPP` kills and removes entity; clears `playerVars` and `connectedPlayers` entries (`Nightvision/Utils.cs:18`)
3. `loadedPlayerCookies.Remove(player.Slot)` clears persistence tracking (`Nightvision/Nightvision.cs:383`)

### Plugin Unload

1. Commands deregistered, CheckTransmit listener removed, event handlers deregistered (`Nightvision/Nightvision.cs:100-104`)
2. `TryUnhookStateTransition` safely unhooks memory function in a try/finally (`Nightvision/Nightvision.cs:106`)
3. `UnsubscribeClientprefsEvents` removes delegate subscriptions to prevent dangling callbacks (`Nightvision/Nightvision.cs:107`)
4. `Utils.RemoveAllPlayerPP` kills and removes all spawned PP volume entities (`Nightvision/Nightvision.cs:108`)
5. All three `Globals` dictionaries cleared; `_oldPlayerState` array zeroed; `loadedPlayerCookies` cleared (`Nightvision/Nightvision.cs:110-113`)
6. Clientprefs state reset: `ClientprefsApi = null`, cookie IDs reset to `-1` (`Nightvision/Nightvision.cs:115-118`)

**State Management:**
- Per-player preferences live in `Globals.playerVars` — `Dictionary<int, PlayerVars>` keyed by `player.Slot`
- Active PP entity handles live in `Globals.postProcessVolumes` — `Dictionary<int, CPostProcessingVolume>` keyed by `player.Slot`
- Connected player controller snapshots live in `Globals.connectedPlayers` — `Dictionary<int, CCSPlayerController>` keyed by `player.Slot`
- Observer state delta tracked in `_oldPlayerState` — `CSPlayerState[65]` on the plugin class, indexed by `player.Index` (pawn entity index, distinct from Slot)
- Cookie load tracking in `loadedPlayerCookies` — `HashSet<int>` of slots that have had their cookies applied this session
- All mutation happens on the CSS main thread within event/hook callbacks; no async/await or background threads

## Key Abstractions

**PlayerVars:**
- Purpose: Minimal per-player domain state required to operate the effect (enabled flag, intensity float)
- Examples: `Nightvision/Classes.cs`
- Pattern: Plain POCO with auto-properties and default values; instantiated on connect, cleared on disconnect

**Slot-keyed Dictionaries (`Globals`):**
- Purpose: Stable cross-callback shared state without holding live entity references in the plugin class; slot is the canonical player identity key
- Examples: `Nightvision/Globals.cs:8-10`
- Pattern: Static `Dictionary<int, T>` — slot keying survives entity invalidation across ticks

**PluginCapability<IClientprefsApi>:**
- Purpose: Optional cross-plugin API discovery; resolved once in `OnAllPluginsLoaded`; null if provider is absent
- Examples: `Nightvision/Nightvision.cs:51`
- Pattern: `PluginCapability<T>.Get()` returns null when absent; every usage guarded by `PersistenceAvailable` or `ClientprefsReady` properties

**MemoryFunctionVoid / StateTransition Hook:**
- Purpose: Hook the native `CCSPlayerPawn::StateTransition` by byte signature to detect observer mode transitions outside the managed event system
- Examples: `Nightvision/Nightvision.cs:59,135`; `Nightvision/nvgamedata.json`
- Pattern: Signature loaded from gamedata JSON at load; `HookMode.Post`; null-safe try/catch on both install and uninstall

**CPostProcessingVolume as NV Effect:**
- Purpose: Per-player game entity that clamps scene exposure to simulate nightvision brightening
- Examples: `Nightvision/Utils.cs:41-54`
- Pattern: Entity spawned with `Master=true`, `ExposureControl=true`, `MaxExposure=MinExposure=intensity`; transmit filtered to owner-only via `CheckTransmit`

## Entry Points

**Plugin Load:**
- Location: `Nightvision/Nightvision.cs:78` (`Load`)
- Triggers: CSS host loads or hot-reloads the plugin
- Responsibilities: Install `StateTransition` hook, register events, listener, commands; sync runtime state on hot reload

**OnAllPluginsLoaded:**
- Location: `Nightvision/Nightvision.cs:164`
- Triggers: CSS host after all plugins are loaded
- Responsibilities: Resolve Clientprefs capability; subscribe to `OnDatabaseLoaded` / `OnPlayerCookiesCached`

**!nv command:**
- Location: `Nightvision/Nightvision.cs:411` (`OnNightvisionCommand`)
- Triggers: Player chat command `!nv` (blocked for bots and spectators)
- Responsibilities: Toggle NV state, create/remove PP entity, persist settings, print result

**!nvi command:**
- Location: `Nightvision/Nightvision.cs:427` (`OnNightvisionIntensityCommand`)
- Triggers: Player chat command `!nvi <float>` (blocked if NV disabled)
- Responsibilities: Validate intensity (0.1–10.0), update `PlayerVars`, recreate PP entity, persist settings

## Architectural Constraints

- **Threading:** Single-threaded CSS event loop. All callbacks (events, hooks, CheckTransmit) run on the CSS main thread. No background threads or `async`/`await`.
- **Global state:** Three static fields on `Globals` (`Nightvision/Globals.cs:8-10`); static offset fields on `CServerSideClient` (`Nightvision/ForceFullUpdate.cs:67`) and `INetworkGameServer` (`Nightvision/ForceFullUpdate.cs:47`). All mutation is main-thread-only.
- **Circular imports:** None. Dependency direction: `Nightvision.cs` → `Globals`, `Utils`, ForceFullUpdate types; `Utils.cs` → `Globals`. No cycles.
- **Unsafe code:** `CServerSideClient.ForceWaitForTick` property uses `unsafe` pointer arithmetic (`Nightvision/ForceFullUpdate.cs:70-72`). Requires `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` in `Nightvision/Nightvision.csproj:6`.
- **Gamedata coupling:** `StateTransition` byte signature and three numeric offsets in `Nightvision/nvgamedata.json` must stay current with the CS2 server binary. Stale signatures degrade gracefully (observer-mode refresh disabled) and are logged at error level. Note: `CServerSideClient_m_nForceWaitForTick` referenced in `Nightvision/ForceFullUpdate.cs:67` is **not present** in `nvgamedata.json` — it must be provided by CSS-shipped gamedata or another deployed source.
- **Index vs Slot:** `_oldPlayerState` is indexed by `player.Index` (pawn entity index, 1-based, up to 64); all `Globals` dictionaries use `player.Slot` (connection slot, 0-based, up to 63). These are different values and must not be confused.

## Anti-Patterns

### Holding `CCSPlayerController` References Across Ticks

**What happens:** `Globals.connectedPlayers` stores `new CCSPlayerController(player.Handle)` snapshots. Callers retrieve and use these handles in later callbacks.
**Why it's wrong:** CSS entity handles can become invalid between ticks. Using a stale handle can cause crashes or undefined behaviour.
**Do this instead:** Always check `player.IsValid` before using a retrieved controller, as done in `OnCheckTransmit` (`Nightvision/Nightvision.cs:395`).

### Issuing Commands Before Clientprefs Is Ready

**What happens:** Without the readiness guard, commands would read uninitialised `PlayerVars` defaults and persist them over the real saved values.
**Why it's wrong:** The default NV-off / intensity-3.0 state would be written to the database, silently discarding the player's actual preference.
**Do this instead:** Always gate player-triggered mutations behind `TryGetReadyPlayerVars`, which checks `ClientprefsAvailabilityResolved`, `ClientprefsReady`, and `loadedPlayerCookies` in order (`Nightvision/Nightvision.cs:278-305`).

## Error Handling

**Strategy:** Log-and-degrade. Failures in optional subsystems (Clientprefs unavailable, stale binary signature) are logged; the plugin continues with reduced functionality.

**Patterns:**
- `TryHookStateTransition` wraps signature resolution and hook install in try/catch; logs error and sets `StateTransition = null` on failure (`Nightvision/Nightvision.cs:124-142`)
- `TryUnhookStateTransition` wraps unhook in try/catch with `finally` to guarantee null-out (`Nightvision/Nightvision.cs:145-162`)
- Cookie ID registration failures logged at error level; plugin continues with session-only state (`Nightvision/Nightvision.cs:200-211`)
- `Utils.CreatePlayerPP` null-checks the spawned entity and returns early if spawn failed (`Nightvision/Utils.cs:43`)

## Cross-Cutting Concerns

**Logging:** CSS `BasePlugin.Logger` (`ILogger`). Debug messages gated by `Config.EnableDebug` via the `LogDebug` helper (`Nightvision/Nightvision.cs:70-76`). Errors and warnings always emitted regardless of the flag.
**Validation:** Intensity float validated by `TryNormalizeNightvisionIntensity` (parse + range check 0.1–10.0, invariant culture) (`Nightvision/Nightvision.cs:336`). Chat prefix/color normalized during `OnConfigParsed` (`Nightvision/Nightvision.cs:63-68`).
**Authentication:** None. Commands are open to any non-bot, non-spectator player. No admin flag checks.

---

*Architecture analysis: 2026-05-04*
