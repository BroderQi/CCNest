using System.Net;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CCNest.Core.Proxy;

public enum ProxyProtocol
{
    Http,
    Socks5,
}

/// <summary>A standard upstream proxy (HTTP CONNECT or SOCKS5), optionally authenticated.</summary>
public sealed partial record ProxyEndpoint
{
    public ProxyProtocol Protocol { get; init; } = ProxyProtocol.Socks5;
    public string Host { get; init; } = "";
    public int Port { get; init; }
    public string? Username { get; init; }

    /// <summary>Never serialized; kept in <see cref="Infrastructure.SecretStore"/>.</summary>
    [JsonIgnore]
    public string? Password { get; init; }

    [JsonIgnore]
    public bool HasCredentials => !string.IsNullOrEmpty(Username);

    public override string ToString() =>
        $"{(Protocol == ProxyProtocol.Http ? "http" : "socks5")}://{(HasCredentials ? Username + ":******@" : "")}{FormatHost(Host)}:{Port}";

    public static string FormatHost(string host) =>
        IPAddress.TryParse(host, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{host}]" : host;

    /// <summary>Validates host, port and credential constraints. Returns null when valid.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Host)) return "代理地址不能为空";
        if (Host.Length > 253) return "代理地址过长";
        if (!IPAddress.TryParse(Host, out _) && !HostnameRegex().IsMatch(Host)) return $"代理地址格式无效：{Host}";
        if (Port is < 1 or > 65535) return "端口必须在 1-65535 之间";
        if (Protocol == ProxyProtocol.Socks5)
        {
            // RFC 1929 limits SOCKS5 username/password to 255 bytes each.
            if ((Username?.Length ?? 0) > 255 || (Password?.Length ?? 0) > 255) return "SOCKS5 用户名或密码超过 255 字节";
        }
        if (!string.IsNullOrEmpty(Password) && string.IsNullOrEmpty(Username)) return "填写了密码但未填写用户名";
        if (ContainsControlChars(Username) || ContainsControlChars(Password)) return "用户名或密码包含非法控制字符";
        return null;
    }

    private static bool ContainsControlChars(string? s) => s != null && s.Any(char.IsControl);

    /// <summary>
    /// Parses the formats proxy providers commonly hand out:
    ///   socks5://user:pass@host:port, http://user:pass@host:port,
    ///   host:port:user:pass, user:pass@host:port, host:port, user:pass:host:port (when host/port detectable).
    /// The password may itself contain ':' in the host:port:user:pass form.
    /// </summary>
    public static bool TryParse(string? text, ProxyProtocol defaultProtocol, out ProxyEndpoint endpoint, out string? error)
    {
        endpoint = new ProxyEndpoint();
        error = null;
        var input = (text ?? "").Trim();
        if (input.Length == 0)
        {
            error = "请输入代理信息";
            return false;
        }

        var protocol = defaultProtocol;
        var schemeMatch = SchemeRegex().Match(input);
        if (schemeMatch.Success)
        {
            var scheme = schemeMatch.Groups[1].Value.ToLowerInvariant();
            protocol = scheme switch
            {
                "http" or "https" => ProxyProtocol.Http,
                "socks5" or "socks5h" or "socks" => ProxyProtocol.Socks5,
                _ => (ProxyProtocol)(-1),
            };
            if ((int)protocol == -1)
            {
                error = $"不支持的代理协议：{scheme}（仅支持 HTTP / SOCKS5）";
                return false;
            }
            input = input[schemeMatch.Length..].TrimEnd('/');
        }

        string? user = null, pass = null, hostPort;
        var at = input.LastIndexOf('@');
        if (at >= 0)
        {
            var cred = input[..at];
            hostPort = input[(at + 1)..];
            var colon = cred.IndexOf(':');
            if (colon < 0) user = cred;
            else
            {
                user = cred[..colon];
                pass = cred[(colon + 1)..];
            }
            user = Uri.UnescapeDataString(user);
            pass = pass is null ? null : Uri.UnescapeDataString(pass);
        }
        else
        {
            hostPort = input;
        }

        string host;
        int port;
        if (at < 0)
        {
            // host:port or host:port:user:pass or [v6]:port
            if (hostPort.StartsWith('['))
            {
                var close = hostPort.IndexOf(']');
                if (close < 0) { error = "IPv6 地址缺少 ]"; return false; }
                host = hostPort[1..close];
                var rest = hostPort[(close + 1)..].TrimStart(':').Split(':', 3);
                if (!int.TryParse(rest[0], out port)) { error = "端口无效"; return false; }
                if (rest.Length >= 2) user = rest[1];
                if (rest.Length >= 3) pass = rest[2];
            }
            else
            {
                var parts = hostPort.Split(':');
                if (parts.Length < 2) { error = "缺少端口（格式示例：host:port:用户名:密码）"; return false; }
                if (int.TryParse(parts[1], out port))
                {
                    host = parts[0];
                    if (parts.Length >= 3) user = parts[2];
                    if (parts.Length >= 4) pass = string.Join(':', parts[3..]);
                }
                else if (parts.Length >= 4 && int.TryParse(parts[^1], out port))
                {
                    // user:pass:host:port
                    host = parts[^2];
                    user = parts[0];
                    pass = string.Join(':', parts[1..^2]);
                }
                else
                {
                    error = "端口无效";
                    return false;
                }
            }
        }
        else
        {
            if (hostPort.StartsWith('['))
            {
                var close = hostPort.IndexOf(']');
                if (close < 0) { error = "IPv6 地址缺少 ]"; return false; }
                host = hostPort[1..close];
                if (!int.TryParse(hostPort[(close + 1)..].TrimStart(':'), out port)) { error = "端口无效"; return false; }
            }
            else
            {
                var colon = hostPort.LastIndexOf(':');
                if (colon < 0 || !int.TryParse(hostPort[(colon + 1)..], out port)) { error = "缺少端口"; return false; }
                host = hostPort[..colon];
            }
        }

        endpoint = new ProxyEndpoint
        {
            Protocol = protocol,
            Host = host.Trim(),
            Port = port,
            Username = string.IsNullOrEmpty(user) ? null : user,
            Password = string.IsNullOrEmpty(pass) ? null : pass,
        };
        error = endpoint.Validate();
        return error is null;
    }

    [GeneratedRegex(@"^([a-zA-Z][a-zA-Z0-9]*)://")]
    private static partial Regex SchemeRegex();

    [GeneratedRegex(@"^(?=.{1,253}$)([a-zA-Z0-9_]([a-zA-Z0-9_-]{0,61}[a-zA-Z0-9_])?)(\.[a-zA-Z0-9_]([a-zA-Z0-9_-]{0,61}[a-zA-Z0-9_])?)*$")]
    private static partial Regex HostnameRegex();
}
