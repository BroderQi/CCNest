using YamlDotNet.RepresentationModel;

namespace CCNest.Core.Mihomo;

/// <summary>Values read from a Clash/mihomo YAML configuration (read-only; CCNest never rewrites it).</summary>
public sealed record ClashFileConfig
{
    public string Path { get; init; } = "";
    public int? MixedPort { get; init; }
    public int? Port { get; init; }
    public int? SocksPort { get; init; }
    public string? ExternalController { get; init; }
    public string? ExternalControllerPipe { get; init; }
    public string? Secret { get; init; }
    public bool? TunEnabled { get; init; }
    public string? Mode { get; init; }
    public bool? AllowLan { get; init; }
    public bool? Ipv6 { get; init; }
    public IReadOnlyList<string> ProxyNames { get; init; } = Array.Empty<string>();
}

public static class ClashConfigReader
{
    public static ClashFileConfig? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var info = new FileInfo(path);
            if (info.Length > 20 * 1024 * 1024) return null; // not a sane config
            return Parse(File.ReadAllText(path), path);
        }
        catch (Exception ex)
        {
            Infrastructure.Log.Debug("ClashConfig", $"读取 {path} 失败: {ex.Message}");
            return null;
        }
    }

    public static ClashFileConfig? Parse(string yaml, string path = "")
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) return null;

        string? Str(string key) => root.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s ? s.Value : null;
        int? Int(string key) => int.TryParse(Str(key), out var i) && i > 0 ? i : null;
        bool? Bool(YamlMappingNode node, string key) =>
            node.Children.TryGetValue(new YamlScalarNode(key), out var v) && v is YamlScalarNode s && bool.TryParse(s.Value, out var b) ? b : null;

        bool? tun = null;
        if (root.Children.TryGetValue(new YamlScalarNode("tun"), out var tunNode) && tunNode is YamlMappingNode tunMap)
            tun = Bool(tunMap, "enable");

        var names = new List<string>();
        if (root.Children.TryGetValue(new YamlScalarNode("proxies"), out var proxies) && proxies is YamlSequenceNode seq)
        {
            foreach (var p in seq.Children.OfType<YamlMappingNode>())
                if (p.Children.TryGetValue(new YamlScalarNode("name"), out var n) && n is YamlScalarNode ns && ns.Value != null)
                    names.Add(ns.Value);
        }

        return new ClashFileConfig
        {
            Path = path,
            MixedPort = Int("mixed-port"),
            Port = Int("port"),
            SocksPort = Int("socks-port"),
            ExternalController = NullIfEmpty(Str("external-controller")),
            ExternalControllerPipe = NullIfEmpty(Str("external-controller-pipe")),
            Secret = NullIfEmpty(Str("secret")),
            TunEnabled = tun,
            Mode = Str("mode"),
            AllowLan = Bool(root, "allow-lan"),
            Ipv6 = Bool(root, "ipv6"),
            ProxyNames = names,
        };
    }

    /// <summary>Returns the YAML text of a single named proxy from a config file (for import into CCNest).</summary>
    public static string? ExtractProxyYaml(string yaml, string proxyName)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) return null;
        if (!root.Children.TryGetValue(new YamlScalarNode("proxies"), out var proxies) || proxies is not YamlSequenceNode seq) return null;
        foreach (var p in seq.Children.OfType<YamlMappingNode>())
        {
            if (p.Children.TryGetValue(new YamlScalarNode("name"), out var n) && n is YamlScalarNode ns && ns.Value == proxyName)
            {
                var doc = new YamlDocument(p);
                var outStream = new YamlStream(doc);
                using var writer = new StringWriter();
                outStream.Save(writer, assignAnchors: false);
                var lines = writer.ToString().TrimEnd().Split('\n').ToList();
                while (lines.Count > 0 && lines[^1].Trim() is "..." or "---") lines.RemoveAt(lines.Count - 1);
                return string.Join('\n', lines).Trim();
            }
        }
        return null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
