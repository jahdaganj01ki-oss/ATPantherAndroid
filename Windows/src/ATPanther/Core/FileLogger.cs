using System.IO;

namespace ATPanther.Core;

public static class FileLogger
{
    private static readonly object _sync = new();
    private static string? _logPath;
    private static bool _initialized;

    public static void Init()
    {
        if (_initialized) return;
        lock (_sync)
        {
            if (_initialized) return;
            try
            {
                var exeDir = AppContext.BaseDirectory;
                _logPath = Path.Combine(exeDir, "at-panther.log");
                Log("INFO", "FileLogger initialized");
                _initialized = true;
            }
            catch { _logPath = null; }
        }
    }

    public static void Log(string level, string message)
    {
        try
        {
            if (_logPath == null) return;
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {message}";
            lock (_sync) File.AppendAllText(_logPath, line + Environment.NewLine);
        }
        catch { }
    }

    public static void Info(string message) => Log("INFO", message);
    public static void Warning(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);
    public static void Error(Exception ex) => Log("ERROR", $"{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
}
