using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace CCNest.Core.Infrastructure;

/// <summary>
/// Removes secrets from any text before it is logged or exported. Known secret values are
/// registered at runtime; common credential patterns are also masked defensively.
/// </summary>
public static partial class SecretRedactor
{
    private static readonly ConcurrentDictionary<string, byte> Secrets = new();
    public const string Mask = "******";

    public static void Register(string? secret)
    {
        if (!string.IsNullOrEmpty(secret) && secret.Length >= 3)
            Secrets[secret] = 0;
    }

    public static void Unregister(string? secret)
    {
        if (!string.IsNullOrEmpty(secret)) Secrets.TryRemove(secret, out _);
    }

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
        var result = text;
        foreach (var secret in Secrets.Keys.OrderByDescending(s => s.Length))
            result = result.Replace(secret, Mask, StringComparison.Ordinal);

        result = UrlCredentialRegex().Replace(result, m => $"{m.Groups["scheme"].Value}{m.Groups["user"].Value}:{Mask}@");
        result = KeyValueRegex().Replace(result, m => $"{m.Groups["key"].Value}{m.Groups["sep"].Value}{Mask}");
        result = AuthHeaderRegex().Replace(result, m => $"{m.Groups["h"].Value}{Mask}");
        return result;
    }

    [GeneratedRegex(@"(?<scheme>\b[a-z][a-z0-9+.-]*://)(?<user>[^:/@\s]+):(?<pass>[^@\s/]+)@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentialRegex();

    [GeneratedRegex(@"(?<key>""?(?:password|passwd|secret|token|private-key|uuid|psk|auth-str|obfs-password)""?)(?<sep>\s*[:=]\s*)(?<val>""[^""]*""|'[^']*'|[^\s,;}]+)", RegexOptions.IgnoreCase)]
    private static partial Regex KeyValueRegex();

    [GeneratedRegex(@"(?<h>(?:Proxy-)?Authorization:\s*(?:Basic|Bearer)\s+)\S+", RegexOptions.IgnoreCase)]
    private static partial Regex AuthHeaderRegex();
}
