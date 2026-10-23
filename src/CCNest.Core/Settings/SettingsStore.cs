using CCNest.Core.Infrastructure;
using CCNest.Core.Proxy;

namespace CCNest.Core.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/>. Secrets are kept separately in <see cref="SecretStore"/>.</summary>
public sealed class SettingsStore
{
    private readonly object _lock = new();
    private AppSettings _current;

    public SettingsStore()
    {
        _current = AtomicFile.ReadJson<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
        Migrate(_current);
    }

    public event Action<AppSettings>? Saved;

    public AppSettings Current
    {
        get { lock (_lock) return _current; }
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            AtomicFile.WriteJson(AppPaths.SettingsFile, settings, keepBackup: true);
            _current = settings;
        }
        Saved?.Invoke(settings);
    }

    public void Update(Action<AppSettings> mutate)
    {
        AppSettings copy;
        lock (_lock)
        {
            copy = Clone(_current);
        }
        mutate(copy);
        Save(copy);
    }

    public static AppSettings Clone(AppSettings s) =>
        System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(s, JsonDefaults.Options), JsonDefaults.Options)!;

    /// <summary>Returns the second-stage endpoint with its password filled in from the secret store.</summary>
    public static ProxyEndpoint ResolveSecondStage(AppSettings s) =>
        s.SecondStage.Endpoint with { Password = SecretStore.Get(SecretStore.SecondStagePassword) };

    public static ProxyEndpoint ResolveManualFirstStage(AppSettings s) =>
        s.FirstStage.Manual with { Password = SecretStore.Get(SecretStore.FirstStagePassword) };

    private static void Migrate(AppSettings s)
    {
        if (s.SchemaVersion < 1) s.SchemaVersion = 1;
        s.Monitor.HealthIntervalSeconds = Math.Clamp(s.Monitor.HealthIntervalSeconds, 5, 600);
        s.Monitor.ExitIpIntervalSeconds = Math.Clamp(s.Monitor.ExitIpIntervalSeconds, 30, 3600);
        s.Monitor.FailureThreshold = Math.Clamp(s.Monitor.FailureThreshold, 1, 10);
        if (s.Advanced.MixedPort is < 1024 or > 65535) s.Advanced.MixedPort = 17897;
    }
}
