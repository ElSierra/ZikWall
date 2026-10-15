internal static class BrokerLifetimeTests
{
    internal static void Run()
    {
        var clock = new SuspendableClock();
        var idle = new BrokerIdleTimeout(clock);
        clock.AdvanceAwake(TimeSpan.FromSeconds(5));
        clock.Utc += TimeSpan.FromHours(8); // Hibernation advances UTC, but not awake time.
        if (idle.IsExpired)
            throw new Exception("Hibernation expired the broker idle timeout: its loop closes the pipe on resume.");

        idle.RecordActivity(); // First heartbeat after resume.
        clock.AdvanceAwake(TimeSpan.FromSeconds(19));
        if (idle.IsExpired) throw new Exception("The post-resume heartbeat did not reset the idle timeout.");
        clock.AdvanceAwake(TimeSpan.FromSeconds(1));
        if (!idle.IsExpired) throw new Exception("A genuinely idle client must still time out after 20 awake seconds.");

        idle.RecordActivity();
        clock.Utc -= TimeSpan.FromDays(1); // Clock correction must not extend the session.
        clock.AdvanceAwake(TimeSpan.FromSeconds(20));
        if (!idle.IsExpired) throw new Exception("A backwards wall-clock adjustment disabled idle cleanup.");
        if (OperatingSystem.IsWindows())
        {
            long before = WindowsAwakeTimeProvider.Instance.GetTimestamp();
            Thread.Sleep(30);
            if (WindowsAwakeTimeProvider.Instance.GetTimestamp() <= before)
                throw new Exception("The Windows awake-time clock did not advance.");
            if (new BrokerIdleTimeout().IsExpired)
                throw new Exception("A newly created production idle timeout is already expired.");
        }
        Console.WriteLine("PASS: broker survives hibernation, accepts a resume heartbeat, and still expires after 20 awake idle seconds.");
    }

    private sealed class SuspendableClock : TimeProvider
    {
        internal DateTimeOffset Utc = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
        private long awakeTicks;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => awakeTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        internal void AdvanceAwake(TimeSpan duration) { Utc += duration; awakeTicks += duration.Ticks; }
    }
}
