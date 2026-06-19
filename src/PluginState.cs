using CounterStrikeSharp.API.Core;

namespace Nightvision;

public sealed class PluginState
{
    public readonly HashSet<int> connectedSlots = [];
    public readonly Dictionary<int, CPostProcessingVolume> postProcessVolumes = new();
    public readonly Dictionary<int, PlayerVars> playerVars = [];
}