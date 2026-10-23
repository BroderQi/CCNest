namespace CCNest.Core.Mihomo;

/// <summary>
/// Domains used by Claude Web, Claude Desktop and Claude Code. Sources: Anthropic's published
/// network requirements for Claude Desktop and the Claude Code network configuration docs.
/// Used only for the optional "Claude only" routing scope; the default scope routes everything.
/// </summary>
public static class ClaudeDomains
{
    /// <summary>First-party domains (matched with DOMAIN-SUFFIX, i.e. including all subdomains).</summary>
    public static readonly string[] FirstParty =
    {
        "anthropic.com",
        "claude.ai",
        "claude.com",
        "claude.app",
        "claudeusercontent.com",
        "claudemcpcontent.com",
    };

    /// <summary>
    /// Third-party services that claude.ai pages and sign-in flows load (bot challenges, payments,
    /// support widget, feature flags). Routing them with Claude keeps one consistent exit for the
    /// whole page. This list can change at any time; users can add domains in advanced settings.
    /// </summary>
    public static readonly string[] Supporting =
    {
        "challenges.cloudflare.com",
        "js.stripe.com",
        "m.stripe.network",
        "api.stripe.com",
        "intercom.io",
        "intercomcdn.com",
        "intercomassets.com",
        "featureassets.org",
        "prodregistryv2.org",
        "statsig.com",
        "statsigapi.net",
        "sentry.io",
        "accounts.google.com",
        "appleid.apple.com",
    };

    /// <summary>Hosts used by CCNest for exit verification; always routed through the second stage.</summary>
    public static readonly string[] VerificationHosts =
    {
        "ipify.org",
        "icanhazip.com",
        "cloudflare.com",
        "ipapi.is",
        "ipwho.is",
        "ipinfo.io",
    };

    public static IEnumerable<string> All(IEnumerable<string>? extra = null) =>
        FirstParty.Concat(Supporting).Concat(VerificationHosts).Concat(extra ?? Array.Empty<string>())
            .Select(d => d.Trim().TrimStart('.').ToLowerInvariant())
            .Where(d => d.Length > 0)
            .Distinct();
}
