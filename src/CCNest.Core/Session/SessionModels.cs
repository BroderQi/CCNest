using CCNest.Core.Proxy;
using CCNest.Core.Settings;

namespace CCNest.Core.Session;

public enum SessionState
{
    Inactive,
    Activating,
    /// <summary>Chain verified, exit matches, gate open.</summary>
    Protected,
    /// <summary>Gate deliberately closed (exit mismatch, re-verification after wake/network change).</summary>
    Blocked,
    /// <summary>The second stage (or core) is failing; traffic fails closed, nothing leaks.</summary>
    Degraded,
    Deactivating,
}

public sealed record SessionStatusInfo
{
    public SessionState State { get; init; } = SessionState.Inactive;
    public string Headline { get; init; } = "未激活";
    public string Detail { get; init; } = "Claude 专属环境尚未激活。";
    public string? ExitIp { get; init; }
    public string? ExpectedIp { get; init; }
    public string? ExitCountry { get; init; }
    public string? FirstStageExitIp { get; init; }
    public int? LatencyMs { get; init; }
    public DateTime? LastCheck { get; init; }
    public DateTime? Since { get; init; }
    public bool GateOpen { get; init; }
    public ProtectionLevel Level { get; init; }
    public RoutingScope Scope { get; init; }
    public int MixedPort { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public IpIntel? Intel { get; init; }
}

public sealed record ActivationStep(string Title, StepStatus Status, string Detail);

public sealed record ActivationResult(bool Success, string Message, IReadOnlyList<ActivationStep> Steps, ChainTestResult? ChainTest,
    IReadOnlyList<string> Notices);

public sealed record DeactivationResult(bool Success, IReadOnlyList<RestoreOutcome> Outcomes, IReadOnlyList<string> Notices)
{
    public bool NeedsSignOut => Outcomes.Any(o => o.Success && o.Timing is Core.SystemEnv.EffectTiming.SignOut);
    public IEnumerable<RestoreOutcome> Problems => Outcomes.Where(o => !o.Success);
}

/// <summary>What is actually guaranteed for each kind of application in the current configuration.</summary>
public sealed record ProtectionScopeItem(string Target, string Coverage, string Guarantee, bool Enforced);
