using System.Text.Json.Nodes;
using CCNest.Core.Mihomo;
using CCNest.Core.Proxy;
using CCNest.Core.Settings;

namespace CCNest.Tests;

public class MihomoConfigBuilderTests
{
    private static CoreConfigSpec Spec(FirstStageMode mode = FirstStageMode.ClashLocalPort, bool tun = false, RoutingScope scope = RoutingScope.AllTraffic) => new()
    {
        MixedPort = 17897,
        ProbeChainPort = 20001,
        ProbeFirstPort = mode == FirstStageMode.None ? null : 20002,
        ControllerPort = 20003,
        Secret = new string('a', 64),
        FirstStageMode = mode,
        FirstStageEndpoint = mode is FirstStageMode.ClashLocalPort or FirstStageMode.ManualProxy
            ? new ProxyEndpoint { Protocol = ProxyProtocol.Socks5, Host = "127.0.0.1", Port = 7897 } : null,
        FirstStageNode = mode == FirstStageMode.ImportedNode ? MihomoConfigBuilder.NodeFromYaml("name: jp\ntype: trojan\nserver: a.example\nport: 443\npassword: '0123'") : null,
        SecondStage = new ProxyEndpoint { Protocol = ProxyProtocol.Http, Host = "isp.example.com", Port = 8000, Username = "u", Password = "p\"w:#{x}' -" },
        Scope = scope,
        Tun = tun,
        DirectProcessNames = tun ? new[] { "verge-mihomo.exe" } : Array.Empty<string>(),
        StartGateClosed = true,
    };

    [Fact]
    public void Gate_HasNoDirectMember_AndStartsClosed()
    {
        var root = MihomoConfigBuilder.BuildNode(Spec());
        var gate = root["proxy-groups"]!.AsArray().Single()!.AsObject();
        var members = gate["proxies"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        Assert.Equal(new[] { "REJECT", MihomoConfigBuilder.StaticName }, members);
        Assert.DoesNotContain("DIRECT", members);
        Assert.Equal("select", gate["type"]!.GetValue<string>());
    }

    [Fact]
    public void Static_DialsThroughFirstStage()
    {
        var root = MihomoConfigBuilder.BuildNode(Spec());
        var proxies = root["proxies"]!.AsArray().Select(p => p!.AsObject()).ToList();
        var first = proxies.Single(p => p["name"]!.GetValue<string>() == MihomoConfigBuilder.FirstName);
        var stat = proxies.Single(p => p["name"]!.GetValue<string>() == MihomoConfigBuilder.StaticName);
        Assert.Equal("socks5", first["type"]!.GetValue<string>());
        Assert.Equal(MihomoConfigBuilder.FirstName, stat["dialer-proxy"]!.GetValue<string>());
        Assert.Equal("p\"w:#{x}' -", stat["password"]!.GetValue<string>()); // exact, no YAML escaping issues
    }

    [Fact]
    public void AllTraffic_EndsWithGateMatch_AndControllerIsLoopbackOnly()
    {
        var root = MihomoConfigBuilder.BuildNode(Spec());
        var rules = root["rules"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        Assert.Equal($"MATCH,{MihomoConfigBuilder.GateName}", rules.Last());
        Assert.StartsWith("127.0.0.1:", root["external-controller"]!.GetValue<string>());
        Assert.False(root["allow-lan"]!.GetValue<bool>());
        Assert.Null(root["tun"]);
    }

    [Fact]
    public void ClaudeOnly_RoutesClaudeDomainsToGate_RestToFirstStage()
    {
        var rules = MihomoConfigBuilder.BuildNode(Spec(scope: RoutingScope.ClaudeOnly))["rules"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        Assert.Contains($"DOMAIN-SUFFIX,claude.ai,{MihomoConfigBuilder.GateName}", rules);
        Assert.Contains($"DOMAIN-SUFFIX,anthropic.com,{MihomoConfigBuilder.GateName}", rules);
        Assert.Equal($"MATCH,{MihomoConfigBuilder.FirstName}", rules.Last());
    }

    [Fact]
    public void Tun_ExcludesFirstStageCoreProcess_AndUsesFakeIp()
    {
        var root = MihomoConfigBuilder.BuildNode(Spec(tun: true));
        var rules = root["rules"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();
        Assert.Equal("PROCESS-NAME,verge-mihomo.exe,DIRECT", rules.First());
        Assert.True(root["tun"]!["strict-route"]!.GetValue<bool>());
        Assert.Equal("fake-ip", root["dns"]!["enhanced-mode"]!.GetValue<string>());
        Assert.False(root["dns"]!["ipv6"]!.GetValue<bool>());
        Assert.Equal("strict", root["find-process-mode"]!.GetValue<string>());
    }

    [Fact]
    public void NoFirstStage_StaticHasNoDialerProxy()
    {
        var root = MihomoConfigBuilder.BuildNode(Spec(FirstStageMode.None));
        var proxies = root["proxies"]!.AsArray();
        Assert.Single(proxies);
        Assert.Null(proxies[0]!["dialer-proxy"]);
    }

    [Fact]
    public void ImportedNode_IsRenamed_AndQuotedScalarsStayStrings()
    {
        var root = MihomoConfigBuilder.BuildNode(Spec(FirstStageMode.ImportedNode));
        var first = root["proxies"]!.AsArray()[0]!.AsObject();
        Assert.Equal(MihomoConfigBuilder.FirstName, first["name"]!.GetValue<string>());
        Assert.Equal("0123", first["password"]!.GetValue<string>());
        Assert.Equal(443, first["port"]!.GetValue<long>());
    }

    [Fact]
    public void NodeFromYaml_AcceptsListAndProxiesWrapper()
    {
        var a = MihomoConfigBuilder.NodeFromYaml("- {name: a, type: ss, server: 1.2.3.4, port: 8388, cipher: aes-128-gcm, password: x}");
        Assert.Equal("ss", a["type"]!.GetValue<string>());
        var b = MihomoConfigBuilder.NodeFromYaml("proxies:\n  - name: b\n    type: vmess\n    server: h\n    port: 1\n    uuid: \"00000000-0000-0000-0000-000000000000\"");
        Assert.Equal("vmess", b["type"]!.GetValue<string>());
    }

    [Fact]
    public void UnsupportedImportedType_IsRejected()
    {
        var spec = Spec(FirstStageMode.ImportedNode) with { FirstStageNode = JsonNode.Parse("{\"name\":\"x\",\"type\":\"direct\",\"server\":\"a\",\"port\":1}")!.AsObject() };
        Assert.Throws<ArgumentException>(() => MihomoConfigBuilder.Build(spec));
    }

    [Fact]
    public void PortConflicts_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => MihomoConfigBuilder.Build(Spec() with { ProbeChainPort = 17897 }));
    }

    [Fact]
    public void ElevatedHelper_RejectsUnsafeConfigs()
    {
        var good = MihomoConfigBuilder.Build(Spec(tun: true));
        Core.Elevation.HelperServer.ValidateTunConfig(good);
        var bad = JsonNode.Parse(good)!.AsObject();
        bad["external-controller"] = "0.0.0.0:9090";
        Assert.Throws<InvalidDataException>(() => Core.Elevation.HelperServer.ValidateTunConfig(bad.ToJsonString()));
        var bad2 = JsonNode.Parse(good)!.AsObject();
        bad2["external-ui"] = "C:\\x";
        Assert.Throws<InvalidDataException>(() => Core.Elevation.HelperServer.ValidateTunConfig(bad2.ToJsonString()));
    }
}
