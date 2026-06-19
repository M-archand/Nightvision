using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace Nightvision;

public class Utils
{
    public static void OnPlayerDisconnect(PluginState state, CCSPlayerController? player)
    {
        if (player == null || player.IsBot)
            return;

        RemovePlayerPP(state, player);
        state.playerVars.Remove(player.Slot);
        state.connectedSlots.Remove(player.Slot);
    }

    public static void CreatePlayerPP(PluginState state, CCSPlayerController? player)
    {
        if (player == null || player.IsBot)
            return;

        int playerSlot = player.Slot;

        if (!state.playerVars.TryGetValue(playerSlot, out var playerVars))
            return;

        RemovePlayerPP(state, player);

        var pp = Utilities.CreateEntityByName<CPostProcessingVolume>("post_processing_volume");
        if (pp == null)
            return;

        pp.Master = true;

        pp.FadeDuration = 0f;
        pp.ExposureControl = true;
        pp.MaxExposure = playerVars.NightvisionIntensity;
        pp.MinExposure = playerVars.NightvisionIntensity;

        pp.DispatchSpawn();

        state.postProcessVolumes[playerSlot] = pp;
    }

    public static void RemovePlayerPP(PluginState state, CCSPlayerController? player)
    {
        if (player == null)
            return;

        int playerSlot = player.Slot;

        if (state.postProcessVolumes.TryGetValue(playerSlot, out var pp))
        {
            if (pp != null && pp.IsValid)
            {
                pp.AcceptInput("Kill");
                pp.Remove();
            }
            state.postProcessVolumes.Remove(playerSlot);
        }
    }

    public static void RemoveAllPlayerPP(PluginState state)
    {
        foreach (var pp in state.postProcessVolumes.Values)
        {
            if (pp != null && pp.IsValid)
            {
                pp.AcceptInput("Kill");
                pp.Remove();
            }
        }

        state.postProcessVolumes.Clear();
    }
}