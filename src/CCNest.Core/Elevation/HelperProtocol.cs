using System.Text.Json;
using System.Text.Json.Nodes;
using CCNest.Core.Infrastructure;
using CCNest.Core.Settings;
using CCNest.Core.Wfp;

namespace CCNest.Core.Elevation;

public static class HelperCommands
{
    public const string Hello = "hello";
    public const string ApplyKillSwitch = "applyKillSwitch";
    public const string RemoveKillSwitch = "removeKillSwitch";
    public const string KillSwitchStatus = "killSwitchStatus";
    public const string StartTunCore = "startTunCore";
    public const string StopTunCore = "stopTunCore";
    public const string TunCoreStatus = "tunCoreStatus";
    public const string SetCrashPolicy = "setCrashPolicy";
    public const string Shutdown = "shutdown";
}

public sealed class HelperRequest
{
    public int Id { get; set; }
    public string Command { get; set; } = "";
    public JsonNode? Payload { get; set; }
}

public sealed class HelperResponse
{
    public int Id { get; set; }
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public JsonNode? Payload { get; set; }
}

public sealed class KillSwitchRequest
{
    public KillSwitchMode Mode { get; set; }
    public bool Dynamic { get; set; }
    public bool AllowLan { get; set; } = true;
    public List<string> AllowedAppPaths { get; set; } = new();
    /// <summary>PIDs whose image paths the elevated helper resolves (e.g. a SYSTEM-service Clash core).</summary>
    public List<int> AllowedProcessIds { get; set; } = new();
    public List<string> ScopedAppPaths { get; set; } = new();
    public List<int> BlockedLoopbackPorts { get; set; } = new();
    /// <summary>Include the CCNest TUN adapter (by alias) as an allowed interface.</summary>
    public string? TunAlias { get; set; }
    /// <summary>Allow CCNest's elevated core binary.</summary>
    public bool AllowElevatedCore { get; set; }
}

public sealed class StartTunCoreRequest
{
    public string Config { get; set; } = "";
    public string TunAlias { get; set; } = "CCNest";
    public string SourceBinary { get; set; } = "";
}

public sealed class CrashPolicyRequest
{
    public CrashPolicy Policy { get; set; }
}

internal static class HelperJson
{
    public static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, JsonDefaults.Options);
    public static T? FromNode<T>(JsonNode? node) => node == null ? default : node.Deserialize<T>(JsonDefaults.Options);
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonDefaults.Options) { WriteIndented = false });
    public static T? Deserialize<T>(string line) => JsonSerializer.Deserialize<T>(line, JsonDefaults.Options);
}
