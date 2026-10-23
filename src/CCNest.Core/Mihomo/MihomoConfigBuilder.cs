using System.Text.Json;
using System.Text.Json.Nodes;
using CCNest.Core.Proxy;
using CCNest.Core.Settings;

namespace CCNest.Core.Mihomo;

/// <summary>Everything needed to render CCNest's private mihomo configuration.</summary>
public sealed record CoreConfigSpec
{
    public required int MixedPort { get; init; }
    public required int ProbeChainPort { get; init; }
    public int? ProbeFirstPort { get; init; }
    public required int ControllerPort { get; init; }
    public required string Secret { get; init; }

    public required FirstStageMode FirstStageMode { get; init; }
    /// <summary>For ClashLocalPort / ManualProxy.</summary>
    public ProxyEndpoint? FirstStageEndpoint { get; init; }
    /// <summary>For ImportedNode: the node mapping (YAML converted to JSON).</summary>
    public JsonObject? FirstStageNode { get; init; }

    public required ProxyEndpoint SecondStage { get; init; }
    public bool SecondStageUdp { get; init; }

    public RoutingScope Scope { get; init; } = RoutingScope.AllTraffic;
    public IReadOnlyList<string> ExtraClaudeDomains { get; init; } = Array.Empty<string>();
    public bool AllowLan { get; init; } = true;

    /// <summary>Gate closed at start (all traffic rejected until verification succeeds).</summary>
    public bool StartGateClosed { get; init; }

    // TUN mode
    public bool Tun { get; init; }
    public string TunDevice { get; init; } = "CCNest";
    /// <summary>Process names (e.g. verge-mihomo.exe) whose traffic must bypass CCNest (the first-stage client itself).</summary>
    public IReadOnlyList<string> DirectProcessNames { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DirectProcessPaths { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Renders the mihomo configuration as JSON (a strict subset of YAML, so mihomo accepts it and no
/// YAML quoting/escaping bugs are possible with arbitrary passwords).
///
/// Design guarantees encoded here:
///  * The second-stage proxy dials through the first stage via <c>dialer-proxy</c>.
///  * Traffic for the protected scope goes to the <c>CCNest-Gate</c> selector, whose only members are
///    the chained proxy and REJECT. There is no DIRECT member and no automatic fallback, so a failed
///    second stage fails closed instead of silently leaking through the first stage or DIRECT.
///  * Probe listeners bypass rules and the gate, letting CCNest verify the exit while the gate is closed.
/// </summary>
public static class MihomoConfigBuilder
{
    public const string FirstName = "CCNest-First";
    public const string StaticName = "CCNest-Static";
    public const string GateName = "CCNest-Gate";
    public const string FakeIpRange = "198.19.0.1/16";

    private static readonly HashSet<string> AllowedNodeTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ss", "ssr", "vmess", "vless", "trojan", "hysteria", "hysteria2", "tuic", "wireguard",
        "http", "socks5", "snell", "anytls", "ssh", "mieru",
    };

    private static readonly string[] LanCidrs =
    {
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10",
    };

    private static readonly string[] LanCidrsV6 = { "fc00::/7", "fe80::/10" };

    public static string Build(CoreConfigSpec spec) => BuildNode(spec).ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public static JsonObject BuildNode(CoreConfigSpec spec)
    {
        Validate(spec);

        var root = new JsonObject
        {
            ["mixed-port"] = spec.MixedPort,
            ["allow-lan"] = false,
            ["bind-address"] = "127.0.0.1",
            ["mode"] = "rule",
            ["log-level"] = "warning",
            ["ipv6"] = spec.Tun,
            ["unified-delay"] = true,
            ["tcp-concurrent"] = false,
            ["geo-auto-update"] = false,
            ["find-process-mode"] = spec.Tun && (spec.DirectProcessNames.Count + spec.DirectProcessPaths.Count) > 0 ? "strict" : "off",
            ["external-controller"] = $"127.0.0.1:{spec.ControllerPort}",
            ["secret"] = spec.Secret,
            ["external-controller-cors"] = new JsonObject
            {
                ["allow-origins"] = new JsonArray("http://127.0.0.1"),
                ["allow-private-network"] = false,
            },
            ["profile"] = new JsonObject { ["store-selected"] = false, ["store-fake-ip"] = false },
        };

        var proxies = new JsonArray();
        var hasFirst = spec.FirstStageMode != FirstStageMode.None;
        if (hasFirst) proxies.Add(BuildFirstStage(spec));
        proxies.Add(BuildStatic(spec, hasFirst));
        root["proxies"] = proxies;

        root["proxy-groups"] = new JsonArray(new JsonObject
        {
            ["name"] = GateName,
            ["type"] = "select",
            ["proxies"] = spec.StartGateClosed ? new JsonArray("REJECT", StaticName) : new JsonArray(StaticName, "REJECT"),
        });

        var listeners = new JsonArray(new JsonObject
        {
            ["name"] = "ccnest-probe-chain",
            ["type"] = "mixed",
            ["listen"] = "127.0.0.1",
            ["port"] = spec.ProbeChainPort,
            ["udp"] = false,
            ["proxy"] = StaticName,
        });
        if (hasFirst && spec.ProbeFirstPort is { } firstPort)
        {
            listeners.Add(new JsonObject
            {
                ["name"] = "ccnest-probe-first",
                ["type"] = "mixed",
                ["listen"] = "127.0.0.1",
                ["port"] = firstPort,
                ["udp"] = false,
                ["proxy"] = FirstName,
            });
        }
        root["listeners"] = listeners;

        root["rules"] = BuildRules(spec, hasFirst);

        if (spec.Tun)
        {
            var tun = new JsonObject
            {
                ["enable"] = true,
                ["device"] = spec.TunDevice,
                ["stack"] = "gvisor",
                ["auto-route"] = true,
                ["auto-detect-interface"] = true,
                ["strict-route"] = true,
                ["dns-hijack"] = new JsonArray("any:53", "tcp://any:53"),
                ["inet6-address"] = new JsonArray("fdfe:dcba:9876::1/126"),
            };
            if (spec.AllowLan)
                tun["route-exclude-address"] = new JsonArray(LanCidrs.Concat(LanCidrsV6).Select(c => (JsonNode)c!).ToArray());
            root["tun"] = tun;

            root["dns"] = new JsonObject
            {
                ["enable"] = true,
                ["ipv6"] = false,
                ["enhanced-mode"] = "fake-ip",
                ["fake-ip-range"] = FakeIpRange,
                ["fake-ip-filter"] = new JsonArray("*.lan", "*.local", "*.localdomain", "+.msftconnecttest.com", "+.msftncsi.com"),
                ["default-nameserver"] = new JsonArray("system"),
                ["nameserver"] = new JsonArray("system"),
                ["respect-rules"] = false,
            };
        }

        return root;
    }

    private static void Validate(CoreConfigSpec spec)
    {
        var ports = new List<int> { spec.MixedPort, spec.ProbeChainPort, spec.ControllerPort };
        if (spec.ProbeFirstPort is { } p) ports.Add(p);
        if (ports.Any(x => x is < 1 or > 65535)) throw new ArgumentException("端口超出范围");
        if (ports.Distinct().Count() != ports.Count) throw new ArgumentException("内核端口冲突");
        if (string.IsNullOrWhiteSpace(spec.Secret) || spec.Secret.Length < 16) throw new ArgumentException("控制器密钥过短");
        var err = spec.SecondStage.Validate();
        if (err != null) throw new ArgumentException("二级代理配置无效：" + err);
        switch (spec.FirstStageMode)
        {
            case FirstStageMode.ClashLocalPort or FirstStageMode.ManualProxy:
                if (spec.FirstStageEndpoint == null) throw new ArgumentException("缺少一级代理配置");
                var e1 = spec.FirstStageEndpoint.Validate();
                if (e1 != null) throw new ArgumentException("一级代理配置无效：" + e1);
                break;
            case FirstStageMode.ImportedNode:
                if (spec.FirstStageNode == null) throw new ArgumentException("缺少导入的一级节点");
                break;
        }
    }

    private static JsonObject BuildFirstStage(CoreConfigSpec spec)
    {
        if (spec.FirstStageMode == FirstStageMode.ImportedNode)
        {
            var node = (JsonObject)spec.FirstStageNode!.DeepClone();
            var type = node["type"]?.GetValue<string>() ?? "";
            if (!AllowedNodeTypes.Contains(type))
                throw new ArgumentException($"不支持的节点类型：{type}");
            if (node["server"] == null || node["port"] == null)
                throw new ArgumentException("导入的节点缺少 server 或 port");
            node["name"] = FirstName;
            node.Remove("dialer-proxy");
            node.Remove("interface-name");
            node.Remove("routing-mark");
            return node;
        }

        return EndpointToProxy(FirstName, spec.FirstStageEndpoint!, udp: true);
    }

    private static JsonObject BuildStatic(CoreConfigSpec spec, bool hasFirst)
    {
        var node = EndpointToProxy(StaticName, spec.SecondStage, udp: spec.SecondStageUdp && spec.SecondStage.Protocol == ProxyProtocol.Socks5);
        if (hasFirst) node["dialer-proxy"] = FirstName;
        return node;
    }

    internal static JsonObject EndpointToProxy(string name, ProxyEndpoint ep, bool udp)
    {
        var node = new JsonObject
        {
            ["name"] = name,
            ["type"] = ep.Protocol == ProxyProtocol.Http ? "http" : "socks5",
            ["server"] = ep.Host,
            ["port"] = ep.Port,
        };
        if (ep.HasCredentials)
        {
            node["username"] = ep.Username;
            node["password"] = ep.Password ?? "";
        }
        if (ep.Protocol == ProxyProtocol.Socks5) node["udp"] = udp;
        return node;
    }

    private static JsonArray BuildRules(CoreConfigSpec spec, bool hasFirst)
    {
        var rules = new JsonArray();
        if (spec.Tun)
        {
            foreach (var path in spec.DirectProcessPaths.Distinct(StringComparer.OrdinalIgnoreCase))
                rules.Add($"PROCESS-PATH,{path},DIRECT");
            foreach (var name in spec.DirectProcessNames.Distinct(StringComparer.OrdinalIgnoreCase))
                rules.Add($"PROCESS-NAME,{name},DIRECT");
            if (spec.AllowLan)
            {
                foreach (var cidr in LanCidrs) rules.Add($"IP-CIDR,{cidr},DIRECT,no-resolve");
                foreach (var cidr in LanCidrsV6) rules.Add($"IP-CIDR6,{cidr},DIRECT,no-resolve");
            }
        }

        if (spec.Scope == RoutingScope.ClaudeOnly)
        {
            foreach (var domain in ClaudeDomains.All(spec.ExtraClaudeDomains))
                rules.Add($"DOMAIN-SUFFIX,{domain},{GateName}");
            rules.Add($"MATCH,{(hasFirst ? FirstName : "DIRECT")}");
        }
        else
        {
            rules.Add($"MATCH,{GateName}");
        }
        return rules;
    }

    /// <summary>Converts a YAML proxy-node mapping (from a Clash config) to JSON for embedding.</summary>
    public static JsonObject NodeFromYaml(string yaml)
    {
        var stream = new YamlDotNet.RepresentationModel.YamlStream();
        try
        {
            stream.Load(new StringReader(yaml));
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ArgumentException($"YAML 格式错误（第 {ex.Start.Line} 行）：{ex.Message}");
        }
        if (stream.Documents.Count == 0) throw new ArgumentException("内容为空");

        var rootNode = stream.Documents[0].RootNode;
        if (rootNode is YamlDotNet.RepresentationModel.YamlSequenceNode seq && seq.Children.Count >= 1)
            rootNode = seq.Children[0];
        if (rootNode is YamlDotNet.RepresentationModel.YamlMappingNode m &&
            m.Children.TryGetValue(new YamlDotNet.RepresentationModel.YamlScalarNode("proxies"), out var proxiesNode) &&
            proxiesNode is YamlDotNet.RepresentationModel.YamlSequenceNode ps && ps.Children.Count >= 1)
            rootNode = ps.Children[0];

        var json = YamlToJson(rootNode) as JsonObject ?? throw new ArgumentException("节点必须是一个 YAML 映射（包含 name/type/server/port）");
        if (json["type"] == null) throw new ArgumentException("节点缺少 type 字段");
        return json;
    }

    /// <summary>
    /// YAML → JSON preserving scalar typing: quoted scalars stay strings (so a password like
    /// "012345" is never turned into a number); plain scalars are typed as YAML 1.2 core schema.
    /// </summary>
    internal static JsonNode? YamlToJson(YamlDotNet.RepresentationModel.YamlNode node) => node switch
    {
        YamlDotNet.RepresentationModel.YamlMappingNode map => new JsonObject(map.Children.Select(kv =>
            new KeyValuePair<string, JsonNode?>(((YamlDotNet.RepresentationModel.YamlScalarNode)kv.Key).Value ?? "", YamlToJson(kv.Value)))),
        YamlDotNet.RepresentationModel.YamlSequenceNode seq => new JsonArray(seq.Children.Select(YamlToJson).ToArray()),
        YamlDotNet.RepresentationModel.YamlScalarNode scalar => ScalarToJson(scalar),
        _ => null,
    };

    private static JsonNode? ScalarToJson(YamlDotNet.RepresentationModel.YamlScalarNode scalar)
    {
        var s = scalar.Value ?? "";
        if (scalar.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted
            or YamlDotNet.Core.ScalarStyle.Literal or YamlDotNet.Core.ScalarStyle.Folded)
            return JsonValue.Create(s);
        if (s is "null" or "Null" or "NULL" or "~" or "") return null;
        if (s is "true" or "True" or "TRUE") return JsonValue.Create(true);
        if (s is "false" or "False" or "FALSE") return JsonValue.Create(false);
        if (long.TryParse(s, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out var l)
            && !(s.TrimStart('-').Length > 1 && s.TrimStart('-').StartsWith('0')))
            return JsonValue.Create(l);
        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            && s.Contains('.') && !s.StartsWith('.') && !s.EndsWith('.'))
            return JsonValue.Create(d);
        return JsonValue.Create(s);
    }
}
