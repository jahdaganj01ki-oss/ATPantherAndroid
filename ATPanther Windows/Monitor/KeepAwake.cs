using System.Runtime.InteropServices;

namespace ATPanther.Monitor;

/// <summary>
/// Pendant zum PARTIAL_WAKE_LOCK des Android-Services
/// (<c>MonitorService.kt:191–245</c>): solange der Monitor läuft, darf das
/// System nicht in den Standby fallen. Der 9-Minuten-Guard des Originals ist
/// nicht nötig, weil die Flag bis zum ausdrücklichen Release bestehen bleibt
/// (PARITY.md 6.5).
/// </summary>
public static class KeepAwake
{
    [Flags]
    private enum ExecutionState : uint
    {
        ES_CONTINUOUS = 0x80000000,
        ES_SYSTEM_REQUIRED = 0x00000001,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    public static bool IsActive { get; private set; }

    public static void Acquire()
    {
        if (IsActive) return;
        try
        {
            SetThreadExecutionState(ExecutionState.ES_CONTINUOUS | ExecutionState.ES_SYSTEM_REQUIRED);
            IsActive = true;
        }
        catch
        {
            // Wie der Originalcode: WakeLock-Probleme werden nur protokolliert,
            // sie dürfen den Monitor nicht stoppen (MonitorService.kt:200–203).
        }
    }

    public static void Release()
    {
        if (!IsActive) return;
        try
        {
            SetThreadExecutionState(ExecutionState.ES_CONTINUOUS);
        }
        catch
        {
            // ignorieren
        }
        IsActive = false;
    }
}

/// <summary>
/// Autostart-Registrierung – Windows-Pendant zu
/// <c>RECEIVE_BOOT_COMPLETED</c> + <c>MonitorWakeReceiver</c>.
/// Startet wie dort nur die Anwendung, nicht den Monitor (PARITY.md 6.3).
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AT Panther";

    public static bool IsEnabled()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, false);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (key == null) return;

            if (enabled)
            {
                string exe = Environment.ProcessPath ?? Application.ExecutablePath;
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                if (key.GetValue(ValueName) != null) key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Registry-Zugriff kann policy-bedingt gesperrt sein; kein Grund zu scheitern.
        }
    }
}
