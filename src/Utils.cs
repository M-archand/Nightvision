using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace Nightvision;

public class Utils
{
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

        state.postProcessVolumes[playerSlot] = new CHandle<CPostProcessingVolume>(pp.EntityHandle.Raw);
    }

    public static void RemovePlayerPP(PluginState state, CCSPlayerController? player)
    {
        if (player == null)
            return;

        RemovePlayerPP(state, player.Slot);
    }

    public static void RemovePlayerPP(PluginState state, int playerSlot)
    {
        if (!state.postProcessVolumes.Remove(playerSlot, out var handle))
            return;

        var pp = handle.Value;
        if (pp != null && pp.IsValid)
            pp.Remove();
    }

    public static void RemoveAllPlayerPP(PluginState state)
    {
        foreach (var handle in state.postProcessVolumes.Values)
        {
            var pp = handle.Value;
            if (pp != null && pp.IsValid)
                pp.Remove();
        }

        state.postProcessVolumes.Clear();
    }
}