using CCNest.Core.Update;

namespace CCNest.Tests;

public class UpdateServiceTests
{
    private const string ReleaseJson = """
    {
      "tag_name": "v1.2.3",
      "html_url": "https://github.com/BroderQi/CCNest/releases/tag/v1.2.3",
      "published_at": "2026-10-20T08:00:00Z",
      "body": "## 下载\n- 便携版",
      "assets": [
        { "name": "CCNest-1.2.3-win-x64-portable.zip", "size": 100, "browser_download_url": "https://github.com/BroderQi/CCNest/releases/download/v1.2.3/CCNest-1.2.3-win-x64-portable.zip" },
        { "name": "CCNest-Setup-1.2.3-x64.exe", "size": 200, "browser_download_url": "https://github.com/BroderQi/CCNest/releases/download/v1.2.3/CCNest-Setup-1.2.3-x64.exe" },
        { "name": "SHA256SUMS.txt", "size": 10, "browser_download_url": "https://github.com/BroderQi/CCNest/releases/download/v1.2.3/SHA256SUMS.txt" },
        { "name": "..\\evil.exe", "size": 1, "browser_download_url": "https://example.com/evil.exe" },
        { "name": "CCNest-Setup-9.9.9-x64.exe", "size": 1, "browser_download_url": "http://insecure.example/setup.exe" }
      ]
    }
    """;

    [Fact]
    public void Release_IsParsed_WithExpectedAssets()
    {
        var info = UpdateService.ParseRelease(ReleaseJson);
        Assert.Equal(new Version(1, 2, 3), info.Version);
        Assert.Equal("1.2.3", info.VersionText);
        Assert.Equal("CCNest-Setup-1.2.3-x64.exe", info.Installer?.Name);
        Assert.Equal("CCNest-1.2.3-win-x64-portable.zip", info.Portable?.Name);
        Assert.Equal("SHA256SUMS.txt", info.Checksums?.Name);
        Assert.Equal(info.Installer, info.AssetFor(InstallKind.Installer));
        Assert.Equal(info.Portable, info.AssetFor(InstallKind.Portable));
        Assert.NotNull(info.PublishedAt);
    }

    [Fact]
    public void Release_WithoutUsableVersion_IsRejected() =>
        Assert.Throws<InvalidOperationException>(() => UpdateService.ParseRelease("""{ "tag_name": "nightly" }"""));

    [Theory]
    [InlineData("v1.0.1", "1.0.1")]
    [InlineData("V2.3", "2.3.0")]
    [InlineData("1.4.0-beta.1", "1.4.0")]
    [InlineData(" 1.0.0 ", "1.0.0")]
    public void Versions_AreParsed(string tag, string expected)
    {
        Assert.True(UpdateService.TryParseVersion(tag, out var v));
        Assert.Equal(Version.Parse(expected), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData(null)]
    public void InvalidVersions_AreRejected(string? tag) => Assert.False(UpdateService.TryParseVersion(tag, out _));

    [Fact]
    public void NewerVersion_ComparesAboveCurrent()
    {
        Assert.True(UpdateService.TryParseVersion("v1.0.10", out var a));
        Assert.True(UpdateService.TryParseVersion("v1.0.9", out var b));
        Assert.True(a > b);
    }

    [Fact]
    public void Checksums_AreParsed()
    {
        var hash1 = new string('a', 64);
        var hash2 = new string('B', 64);
        var sums = UpdateService.ParseChecksums($"﻿{hash1}  CCNest-1.2.3-win-x64-portable.zip\r\n{hash2} *CCNest-Setup-1.2.3-x64.exe\r\nnot a checksum line\r\n");
        Assert.Equal(hash1, sums["CCNest-1.2.3-win-x64-portable.zip"]);
        Assert.Equal(hash2.ToLowerInvariant(), sums["ccnest-setup-1.2.3-x64.exe"]);
        Assert.Equal(2, sums.Count);
    }

    [Fact]
    public void DefaultFeed_PointsAtOfficialRepository()
    {
        Assert.Equal("https://api.github.com/repos/BroderQi/CCNest/releases/latest", UpdateService.ResolveFeed(null));
        Assert.Equal("https://api.github.com/repos/BroderQi/CCNest/releases/latest", UpdateService.ResolveFeed("  "));
        Assert.Equal("https://example.com/feed", UpdateService.ResolveFeed(" https://example.com/feed "));
    }

    [Fact]
    public void ApplyScript_IsAsciiForWindowsPowerShell() =>
        Assert.All(UpdateService.ApplyScript, c => Assert.True(c < 128, $"non-ASCII character U+{(int)c:X4}"));
}
