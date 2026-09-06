using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace ATPanther.Windows;

/// <summary>
/// File-based diagnostics log for failure analysis, next to the monitor's
/// <c>history.log</c> in <c>%LocalAppData%\ATPanther\diagnostics.log</c>.
///
/// Each line carries timestamp, level, area (tag) and message; exceptions are
/// flattened with type, message, stack trace and all inner exceptions.
/// Rotation kicks in at 1 MiB (keeps <c>diagnostics.1.log</c> … <c>.5.log</c>).
/// The logger never throws: diagnostics must not mask the failure they record.
///
/// The functional monitor history (CHECK/BOOKING) stays in
/// <see cref="MonitorController"/>; startup, crashes, catch-all branches and
/// engine trace belong here.
///
/// Note on the "side-by-side configuration is invalid" failure class: the
/// Windows loader raises it BEFORE any managed code runs, so no .NET logger
/// can capture that moment. The build ships <c>Diagnose-AT-Panther.bat</c>
/// (sxstrace flow) for exactly that case, and the startup header below pins
/// down which build/OS a log file came from.
/// </summary>
public static class DiagLog
{
    private const long MaxBytes = 1_048_576;
    private const int KeepRotated = 5;

    private static readonly object Gate = new();
    private static bool headerWritten;

    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ATPanther");

    public static string FilePath => Path.Combine(DataDir, "diagnostics.log");

    /// <summary>
    /// Writes the one-per-process startup header (version, OS, runtime,
    /// bitness, exe path). The first call also creates the directory.
    /// </summary>
    public static void StartupHeader(string phase)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
            }
            catch
            {
                return;
            }

            if (headerWritten) return;
            headerWritten = true;

            string exe;
            try { exe = Environment.ProcessPath ?? string.Empty; }
            catch { exe = string.Empty; }

            string fileVersion;
            try { fileVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion ?? "?"; }
            catch { fileVersion = "?"; }

            string assemblyVersion;
            try { assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?"; }
            catch { assemblyVersion = "?"; }

            var sb = new StringBuilder();
            sb.Append("Start [").Append(phase).Append("] AT Panther ").Append(assemblyVersion)
              .Append(" (file version ").Append(fileVersion).Append(") | ")
              .Append(RuntimeInformation.OSDescription).Append(" (")
              .Append(RuntimeInformation.OSArchitecture).Append(") | process ")
              .Append(Environment.Is64BitProcess ? "x64" : "x86").Append(" | .NET ")
              .Append(RuntimeInformation.FrameworkDescription).Append(" | exe: ").Append(exe);
            WriteLocked("INFO", "Start", sb.ToString());
        }
    }

    public static void Info(string tag, string message) => Write("INFO", tag, message);

    public static void Warn(string tag, string message) => Write("WARN", tag, message);

    public static void Warn(string tag, string message, Exception ex) =>
        Write("WARN", tag, message + Environment.NewLine + Flatten(ex));

    public static void Error(string tag, string message) => Write("ERROR", tag, message);

    public static void Error(string tag, string message, Exception ex) =>
        Write("ERROR", tag, message + Environment.NewLine + Flatten(ex));

    /// <summary>Flattens an exception with all inner exceptions and stack traces.</summary>
    public static string Flatten(Exception ex)
    {
        var sb = new StringBuilder();
        for (Exception? current = ex; current != null; current = current.InnerException)
        {
            sb.Append("  ").Append(current.GetType().FullName).Append(": ").Append(current.Message);
            if (current.StackTrace != null)
            {
                sb.Append(Environment.NewLine).Append("  Stack: ")
                  .Append(current.StackTrace.Trim());
            }
            if (current.InnerException != null) sb.Append(Environment.NewLine).Append("  Caused by:");
        }
        return sb.ToString();
    }

    private static void Write(string level, string tag, string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(DataDir);
                RotateLocked();
                string line = $"{DateTime.Now:dd.MM.yyyy HH:mm:ss.fff} [{level}] [{tag}] {message}{Environment.NewLine}";
                File.AppendAllText(FilePath, line, new UTF8Encoding(false));
            }
            catch
            {
                // Diagnostics must never throw.
            }
        }
    }

    private static void WriteLocked(string level, string tag, string message)
    {
        try
        {
            RotateLocked();
            string line = $"{DateTime.Now:dd.MM.yyyy HH:mm:ss.fff} [{level}] [{tag}] {message}{Environment.NewLine}";
            File.AppendAllText(FilePath, line, new UTF8Encoding(false));
        }
        catch
        {
            // Diagnostics must never throw.
        }
    }

    private static string RotatedPath(int index) =>
        Path.Combine(DataDir, $"diagnostics.{index}.log");

    private static void RotateLocked()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length < MaxBytes) return;

            try { if (File.Exists(RotatedPath(KeepRotated))) File.Delete(RotatedPath(KeepRotated)); }
            catch { /* move on */ }

            for (int i = KeepRotated - 1; i >= 1; i--)
            {
                try
                {
                    if (File.Exists(RotatedPath(i)))
                        File.Move(RotatedPath(i), RotatedPath(i + 1), overwrite: true);
                }
                catch { /* move on */ }
            }

            try { File.Move(FilePath, RotatedPath(1), overwrite: true); } catch { /* move on */ }
        }
        catch
        {
            // Rotation is best effort.
        }
    }
}
