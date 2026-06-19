using CounterStrikeSharp.API.Core;

namespace Nightvision;

public class Globals
{
    public static Dictionary<int, CCSPlayerController> connectedPlayers = [];
    public static Dictionary<int, CPostProcessingVolume> postProcessVolumes = new();
    public static Dictionary<int, PlayerVars> playerVars = [];
}