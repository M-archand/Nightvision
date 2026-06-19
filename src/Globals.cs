using CounterStrikeSharp.API.Core;

namespace Nightvision;

public class Globals
{
    public static HashSet<int> connectedSlots = [];
    public static Dictionary<int, CPostProcessingVolume> postProcessVolumes = new();
    public static Dictionary<int, PlayerVars> playerVars = [];
}