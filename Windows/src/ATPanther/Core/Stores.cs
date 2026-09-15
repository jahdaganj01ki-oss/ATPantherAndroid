using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ATPanther.Core;

public sealed record Credentials(string Phone, string Password, float ThresholdMb, int IntervalSec);

/// <summary>DPAPI-geschuetzter Credential-Store (statt Android-SharedPreferences).</summary>
public sealed class CredentialStore
{
    private readonly string _file;
    public CredentialStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATPanther");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "credentials.dat");
    }

    public Credentials Load() => File.Exists(_file)
        ? JsonSerializer.Deserialize<Credentials>(Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(_file), null, DataProtectionScope.CurrentUser)))!
        : new Credentials("", "", AppConfig.DefaultThresholdMb, AppConfig.DefaultIntervalSec);

    public void Save(Credentials c)
    {
        var raw = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(c));
        File.WriteAllBytes(_file, ProtectedData.Protect(raw, null, DataProtectionScope.CurrentUser));
    }
}

public sealed record MonitorState(int ConnectionFailures, bool PausedAfterFailures);

public sealed class MonitorStateStore
{
    private readonly string _file;
    public MonitorStateStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATPanther");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "monitor_state.json");
    }

    public MonitorState Load() => File.Exists(_file)
        ? JsonSerializer.Deserialize<MonitorState>(File.ReadAllText(_file))!
        : new MonitorState(0, false);

    public void Save(MonitorState s) => File.WriteAllText(_file, JsonSerializer.Serialize(s));
}
