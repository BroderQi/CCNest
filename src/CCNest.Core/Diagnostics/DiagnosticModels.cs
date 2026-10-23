namespace CCNest.Core.Diagnostics;

public enum DiagStatus
{
    Ok,
    Info,
    Warning,
    Error,
    Unknown,
}

public enum DiagCategory
{
    Network,
    Proxy,
    Dns,
    Routing,
    Security,
    Claude,
    System,
    Browser,
}

/// <summary>
/// A supported, reversible repair. <see cref="Changes"/> lists exactly what will be modified so the
/// user can approve it; <see cref="ApplyAsync"/> journals the original values before changing them.
/// </summary>
public sealed record RepairAction(
    string Id,
    string Title,
    string Description,
    IReadOnlyList<string> Changes,
    bool RequiresElevation,
    Func<CancellationToken, Task<string>> ApplyAsync);

/// <summary>
/// One diagnostic finding, structured around the questions the user actually has: what was detected,
/// what works, what is wrong, what CCNest can repair, what needs manual action, what is uncertain.
/// </summary>
public sealed record DiagnosticItem
{
    public required string Id { get; init; }
    public required DiagCategory Category { get; init; }
    public required string Title { get; init; }
    public required DiagStatus Status { get; init; }
    public required string Detected { get; init; }
    public string? Explanation { get; init; }
    public string? ManualSteps { get; init; }
    public string? Uncertainty { get; init; }
    public RepairAction? Repair { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> Details { get; init; } = Array.Empty<KeyValuePair<string, string>>();

    public static string CategoryName(DiagCategory c) => c switch
    {
        DiagCategory.Network => "网络出口",
        DiagCategory.Proxy => "代理线路",
        DiagCategory.Dns => "DNS",
        DiagCategory.Routing => "路由与系统代理",
        DiagCategory.Security => "防护与安全",
        DiagCategory.Claude => "Claude 应用",
        DiagCategory.System => "Windows 环境",
        DiagCategory.Browser => "专属浏览器",
        _ => c.ToString(),
    };
}

public sealed record DiagnosticReport(DateTime StartedAt, TimeSpan Duration, IReadOnlyList<DiagnosticItem> Items)
{
    public int Errors => Items.Count(i => i.Status == DiagStatus.Error);
    public int Warnings => Items.Count(i => i.Status == DiagStatus.Warning);
    public int Repairable => Items.Count(i => i.Repair != null);

    public string ToText()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"CCNest 诊断报告  {StartedAt:yyyy-MM-dd HH:mm:ss}  （用时 {Duration.TotalSeconds:F1} 秒）");
        sb.AppendLine($"CCNest {AppInfo.Version} / Windows {Environment.OSVersion.Version}");
        sb.AppendLine(new string('=', 60));
        foreach (var g in Items.GroupBy(i => i.Category))
        {
            sb.AppendLine();
            sb.AppendLine($"## {DiagnosticItem.CategoryName(g.Key)}");
            foreach (var i in g)
            {
                sb.AppendLine($"[{i.Status}] {i.Title}: {i.Detected}");
                if (i.Explanation != null) sb.AppendLine($"    说明：{i.Explanation}");
                foreach (var d in i.Details) sb.AppendLine($"    - {d.Key}: {d.Value}");
                if (i.Repair != null) sb.AppendLine($"    可修复：{i.Repair.Title}");
                if (i.ManualSteps != null) sb.AppendLine($"    需手动：{i.ManualSteps}");
                if (i.Uncertainty != null) sb.AppendLine($"    不确定性：{i.Uncertainty}");
            }
        }
        return Infrastructure.SecretRedactor.Redact(sb.ToString());
    }
}
