using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace Nightvision;

public sealed class PluginState
{
    public readonly Dictionary<int, CHandle<CPostProcessingVolume>> postProcessVolumes = new();
    public readonly Dictionary<int, PlayerVars> playerVars = [];
}