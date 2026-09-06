using System.Runtime.InteropServices;

namespace ATPanther.Windows;

/// <summary>
/// Windows counterpart to the Android partial wake lock (PowerManager.WAKE_LOCK):
/// keeps the system awake while the monitor loop is running, so Windows sleep or
/// aggressive power management cannot suspend the polling between checks.
/// </summary>
internal static class SystemSleep
{
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsContinuous = 0x80000000;

    private static int _active;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(uint esFlags);

    /// <summary>Prevent system sleep (reference counted within the process).</summary>
    public static void PreventSleep()
    {
        if (Interlocked.Increment(ref _active) == 1)
        {
            _ = SetThreadExecutionState(EsContinuous | EsSystemRequired);
        }
    }

    /// <summary>Release the keep-awake request again.</summary>
    public static void AllowSleep()
    {
        if (Interlocked.Decrement(ref _active) <= 0)
        {
            _active = 0;
            _ = SetThreadExecutionState(EsContinuous);
        }
    }
}
