using CCNest.Core.Mihomo;

namespace CCNest.Tests;

public class ClashConfigReaderTests
{
    private const string VergeRuntime = """
        mode: rule
        mixed-port: 7897
        allow-lan: false
        ipv6: true
        external-controller: ''
        secret: s3cr3t
        external-controller-pipe: \\.\pipe\verge-mihomo
        tun:
          enable: false
          stack: gvisor
        proxies:
        - name: Node-A
          type: socks5
          server: 10.1.2.3
          port: 1080
          password: "000123"
        - name: Node-B
          type: trojan
          server: b.example
          port: 443
          password: pw
        """;

    [Fact]
    public void ParsesVergeRuntimeConfig()
    {
        var cfg = ClashConfigReader.Parse(VergeRuntime)!;
        Assert.Equal(7897, cfg.MixedPort);
        Assert.Null(cfg.ExternalController); // '' means disabled
        Assert.Equal(@"\\.\pipe\verge-mihomo", cfg.ExternalControllerPipe);
        Assert.Equal("s3cr3t", cfg.Secret);
        Assert.False(cfg.TunEnabled);
        Assert.Equal(new[] { "Node-A", "Node-B" }, cfg.ProxyNames);
    }

    [Fact]
    public void ExtractsSingleNode_ForImport_PreservingQuotedPassword()
    {
        var yaml = ClashConfigReader.ExtractProxyYaml(VergeRuntime, "Node-A")!;
        var node = MihomoConfigBuilder.NodeFromYaml(yaml);
        Assert.Equal("Node-A", node["name"]!.GetValue<string>());
        Assert.Equal("000123", node["password"]!.GetValue<string>());
        Assert.Null(ClashConfigReader.ExtractProxyYaml(VergeRuntime, "missing"));
    }

    [Fact]
    public void PipeAddressHelpers()
    {
        Assert.True(MihomoController.IsPipeAddress(@"\\.\pipe\verge-mihomo"));
        Assert.Equal("verge-mihomo", MihomoController.PipeName(@"\\.\pipe\verge-mihomo"));
        Assert.False(MihomoController.IsPipeAddress("127.0.0.1:9097"));
    }
}
