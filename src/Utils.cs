using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace Nightvision;

public class Utils
{
    public static void OnPlayerConnect(CCSPlayerController? player)
    {
        if (player == null || player.IsBot)
            return;

        Globals.playerVars[player.Slot] = new PlayerVars();
        Globals.connectedPlayers[player.Slot] = new CCSPlayerController(player.Handle);
    }

    public static void OnPlayerDisconnect(CCSPlayerController? player)
    {
        if (player == null || player.IsBot)
            return;

        RemovePlayerPP(player);
        Globals.playerVars[player.Slot] = new PlayerVars();
        Globals.playerVars.Remove(player.Slot);
        Globals.connectedPlayers.Remove(player.Slot);
    }

    public static void CreatePlayerPP(CCSPlayerController? player)
    {
        if (player == null || player.IsBot)
            return;

        int playerSlot = player.Slot;

        if (!Globals.playerVars.TryGetValue(playerSlot, out var playerVars))
            return;

        RemovePlayerPP(player);

        var pp = Utilities.CreateEntityByName<CPostProcessingVolume>("post_processing_volume");
        if (pp == null)
            return;

        pp.Master = true;

        pp.FadeDuration = 0f;
        pp.ExposureControl = true;
        pp.MaxExposure = playerVars.NightvisionIntensity;
        pp.MinExposure = playerVars.NightvisionIntensity;
        
        pp.DispatchSpawn();
        
        Globals.postProcessVolumes[playerSlot] = pp;
    }

    public static void RemovePlayerPP(CCSPlayerController? player)
    {
        if (player == null)
            return;

        int playerSlot = player.Slot;

        if (Globals.postProcessVolumes.TryGetValue(playerSlot, out var pp))
        {
            if (pp != null && pp.IsValid)
            {
                pp.AcceptInput("Kill");
                pp.Remove();
            }
            Globals.postProcessVolumes.Remove(playerSlot);
        }
    }

    public static void RemoveAllPlayerPP()
    {
        foreach (var pp in Globals.postProcessVolumes.Values)
        {
            if (pp != null && pp.IsValid)
            {
                pp.AcceptInput("Kill");
                pp.Remove();
            }
        }

        Globals.postProcessVolumes.Clear();
    }
}