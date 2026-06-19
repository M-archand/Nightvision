using System.Text.Json.Serialization;
using CounterStrikeSharp.API.Core;

namespace Nightvision;

public class NightvisionConfig : BasePluginConfig
{
    [JsonPropertyName("EnableDebug")]
    public bool EnableDebug { get; set; } = false;

    [JsonPropertyName("ChatPrefix")]
    public string ChatPrefix { get; set; } = "[NightVision]";

    [JsonPropertyName("ChatPrefixColor")]
    public string ChatPrefixColor { get; set; } = "Lime";
}