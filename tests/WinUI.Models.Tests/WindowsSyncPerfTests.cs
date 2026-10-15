using ZikWall.WinUI.Models;

static class WindowsSyncPerfTests
{
    public static bool Run()
    {
        var id = Guid.NewGuid();
        var profile = new NetworkProfile(id, "Allocation regression", Enumerable.Range(0, 1000).Select(i =>
            new FirewallRule(Guid.NewGuid(), "Fixture " + i, @"C:\Tests\sync.exe", $"10.{i / 256}.{i % 256}.0/24", 6, 0, 1, i % 2 == 0, true, null)).ToList());
        var initial = WindowsFirewallSync.Plan(profile, null, [], []);
        var state = new WindowsFirewallSyncState(id, initial.Links);
        var current = initial.Links.SelectMany(x => x.Windows).ToList();
        _ = WindowsFirewallSync.Plan(profile, state, current, []);
        long start = GC.GetAllocatedBytesForCurrentThread();
        var plan = WindowsFirewallSync.Plan(profile, state, current, []);
        long bytes = GC.GetAllocatedBytesForCurrentThread() - start;
        bool passed = plan.Ready && plan.Summary.Count == 0 && plan.Rules.Count == 1000 && bytes <= 8_000_000;
        Console.WriteLine($"{(passed ? "PASS" : "FAIL")}: 1,000 mixed disjoint rules: {bytes:N0} allocated bytes (budget 8,000,000); unchanged plan = {plan.Ready && plan.Summary.Count == 0}.");
        return passed;
    }
}
