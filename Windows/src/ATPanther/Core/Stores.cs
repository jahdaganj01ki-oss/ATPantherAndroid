using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ATPanther.Core;

namespace ATPanther;

public sealed record Credentials(string Phone, string Password, float ThresholdMb, int IntervalSec);

public sealed class CredentialStore
{
    private readonly string _file;
    public CredentialStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ATPanther");
        Directory.CreateDirectory(dir);
        _file = Path.Combine(dir, "credentials.dat");
    }

    public Credentials Load()
    {
        try
        {
            if (!File.Exists(_file))
                return new Credentials("", "", AppConfig.DefaultThresholdMb, AppConfig.DefaultIntervalSec);

            var encrypted = File.ReadAllBytes(_file);
            var raw = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(raw);
            var creds = JsonSerializer.Deserialize<Credentials>(json);
            return creds ?? new Credentials("", "", AppConfig.DefaultThresholdMb, AppConfig.DefaultIntervalSec);
        }
        catch (CryptographicException)
        {
            FileLogger.Warning($"CredentialStore: invalid encrypted file, deleting {_file}");
            try { File.Delete(_file); } catch { }
            return new Credentials("", "", AppConfig.DefaultThresholdMb, AppConfig.DefaultIntervalSec);
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            return new Credentials("", "", AppConfig.DefaultThresholdMb, AppConfig.DefaultIntervalSec);
        }
    }

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

    public MonitorState Load()
    {
        try
        {
            if (!File.Exists(_file))
                return new MonitorState(0, false);

            var json = File.ReadAllText(_file);
            var state = JsonSerializer.Deserialize<MonitorState>(json);
            return state ?? new MonitorState(0, false);
        }
        catch (Exception ex)
        {
            FileLogger.Error(ex);
            return new MonitorState(0, false);
        }
    }

    public void Save(MonitorState s) => File.WriteAllText(_file, JsonSerializer.Serialize(s));
}
