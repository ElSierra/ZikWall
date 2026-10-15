using System.ComponentModel;
using System.Runtime.InteropServices;

internal sealed class BrokerIdleTimeout(TimeProvider clock)
{
    private long lastRequest = clock.GetTimestamp();

    internal BrokerIdleTimeout() : this(WindowsAwakeTimeProvider.Instance) { }

    internal bool IsExpired => clock.GetElapsedTime(lastRequest) >= TimeSpan.FromSeconds(20);
    internal void RecordActivity() => lastRequest = clock.GetTimestamp();
}

internal sealed class WindowsAwakeTimeProvider : TimeProvider
{
    internal static readonly WindowsAwakeTimeProvider Instance = new();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryUnbiasedInterruptTime(out ulong time);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        // Windows reports 100 ns units of awake time, excluding sleep and hibernation.
        // UTC is still used for rule expiry; only client liveness uses this clock.
        if (!QueryUnbiasedInterruptTime(out ulong time)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return checked((long)time);
    }
}
