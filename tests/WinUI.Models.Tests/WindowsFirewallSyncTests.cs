using ZikWall.WinUI.Models;
using ZikWall.WinUI.Services;

static class WindowsFirewallSyncTests
{
    private sealed class FakeStore(List<WindowsFirewallEntry> entries) : IWindowsFirewallStore
    {
        public List<WindowsFirewallEntry> Entries = entries;
        public int Writes;
        public bool FailAdd;
        public List<WindowsFirewallEntry>? LastSyncTargets;
        public int Reads;
        public Action<FakeStore>? BeforeRead;
        public List<WindowsFirewallEntry> Read() { Reads++; BeforeRead?.Invoke(this); return Entries.ToList(); }
        public List<WindowsFirewallEntry> ReadForSync(IReadOnlyList<WindowsFirewallEntry> linked) { LastSyncTargets = linked.ToList(); return Read(); }
        public void Remove(string name) { Writes++; Entries.RemoveAll(x => x.Name == name); }
        public void Add(WindowsFirewallEntry entry) { if (FailAdd) throw new IOException("Injected interrupted write"); Writes++; Entries.Add(entry); }
    }
    public static void Run()
    {
        int checks = 0;
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
        void Reject(Action action, string message) { try { action(); } catch (InvalidDataException) { checks++; return; } throw new Exception(message); }
        var profileId = Guid.NewGuid();
        var rule = new FirewallRule(Guid.NewGuid(), "Fixture", @"C:\Tests\fixture.exe", "192.0.2.123/24", 6, 443, 3, true, true, null);
        NetworkProfile Profile(params FirewallRule[] rules) => new(profileId, "Sync test", rules.ToList());
        WindowsFirewallEntry Edit(WindowsFirewallEntry entry, FirewallRule changed) => WindowsFirewallEntry.Create(entry.Name, entry.Description, entry.Group, changed);
        var initial = WindowsFirewallSync.Plan(Profile(rule), null, [], []);
        Check(initial.Ready && initial.Changes.Count == 2 && initial.Links.Count == 1, "Both directions must produce two linked Windows rules.");
        var native = initial.Changes.Select(x => x.After!).ToList();
        var state = new WindowsFirewallSyncState(profileId, initial.Links);
        Check(native.Select(x => x.Rule!.Direction).Order().SequenceEqual(new[] { 1, 2 }), "Directions must survive export.");
        Check(native.All(x => x.Rule!.RemoteNetwork == "192.0.2.0/24"), "Networks must be canonicalized.");
        Check(WindowsFirewallSync.NormalizeNetwork("192.0.2.123/255.255.255.0") == "192.0.2.0/24", "Windows dotted subnet masks must normalize.");
        Check(WindowsFirewallSync.NormalizeNetwork("2001:db8::123/64") == "2001:db8::/64", "IPv6 host bits must normalize.");
        Reject(() => WindowsFirewallSync.NormalizeNetwork("192.0.2.1/255.0.255.0"), "Malformed masks accepted.");
        Check(WindowsFirewallSync.Plan(Profile(rule), state, native, []).Summary.Count == 0, "Unchanged snapshots must not cause a sync loop.");
        var localEdit = rule with { RemotePort = 8443, Enabled = false };
        var push = WindowsFirewallSync.Plan(Profile(localEdit), state, native, []);
        Check(push.Ready && push.Changes.Count == 2 && push.Changes.All(x => x.After?.Rule?.RemotePort == 8443 && !x.After.Rule.Enabled), "Local port and disable edits must propagate.");
        var remoteEdit = native.Select(x => Edit(x, x.Rule! with { RemotePort = 9443, Enabled = false })).ToList();
        var pull = WindowsFirewallSync.Plan(Profile(rule), state, remoteEdit, []);
        Check(pull.Ready && pull.Changes.Count == 0 && pull.Rules.Single().RemotePort == 9443 && !pull.Rules.Single().Enabled && pull.Rules.Single().Direction == 3, "Remote edits must merge into the existing local identity.");
        Check(pull.Rules.Single().Id == rule.Id, "Remote imports must preserve linked identity.");
        var conflict = WindowsFirewallSync.Plan(Profile(localEdit), state, remoteEdit, []);
        Check(!conflict.Ready && conflict.Problems.Any(x => x.Contains("both apps")), "Concurrent edits must stop without choosing a winner.");
        var keepLocal = WindowsFirewallSync.Plan(Profile(localEdit), state, remoteEdit, [], WindowsFirewallConflictChoice.ZikWall);
        Check(keepLocal.Ready && keepLocal.Changes.Count == 2 && keepLocal.Rules.Single().RemotePort == 8443, "Local conflict resolution failed.");
        var keepWindows = WindowsFirewallSync.Plan(Profile(localEdit), state, remoteEdit, [], WindowsFirewallConflictChoice.Windows);
        Check(keepWindows.Ready && keepWindows.Changes.Count == 0 && keepWindows.Rules.Single().RemotePort == 9443, "Windows conflict resolution failed.");
        Check(WindowsFirewallSync.Plan(Profile(rule), state, [], []).Rules.Count == 0, "Windows deletion must delete the linked local rule.");
        var deleteLocal = WindowsFirewallSync.Plan(Profile(), state, native, []);
        Check(deleteLocal.Ready && deleteLocal.Changes.Count == 2 && deleteLocal.Changes.All(x => x.After is null), "Local deletion must remove linked Windows rules.");
        Check(!WindowsFirewallSync.Plan(Profile(localEdit), state, [], []).Ready, "Delete versus edit must conflict.");
        Check(WindowsFirewallSync.Plan(Profile(), state, [], []).Ready, "Both-side deletion must converge.");
        var halfDeleted = WindowsFirewallSync.Plan(Profile(rule), state, [native[0]], []);
        Check(halfDeleted.Ready && halfDeleted.Rules.Single().Direction == 1, "Deleting one direction must narrow the linked local rule.");
        var diverged = new List<WindowsFirewallEntry> { remoteEdit[0], native[1] };
        Check(!WindowsFirewallSync.Plan(Profile(rule), state, diverged, []).Ready, "Divergent direction pairs must require review.");
        Check(WindowsFirewallSync.Plan(Profile(rule), state, diverged, [], WindowsFirewallConflictChoice.ZikWall).Ready, "Explicit local resolution must repair a divergent pair.");
        var renamed = native.Select(x => WindowsFirewallEntry.Create("Renamed" + (x.Rule!.Direction == 1 ? " [ZikWall out]" : " [ZikWall in]"), x.Description, x.Group, x.Rule with { Name = "Renamed" })).ToList();
        Check(WindowsFirewallSync.Plan(Profile(rule), state, renamed, []).Rules.Single().Name == "Renamed", "Exported rule renames must match their persistent description identity.");
        var external = WindowsFirewallEntry.Create("External rule", "Original description", "Third party", rule with { Name = "External rule", Direction = 1 });
        var import = WindowsFirewallSync.Plan(Profile(), null, [external], [external]);
        Check(import.Ready && import.Changes.Count == 0 && import.Rules.Single().Name == "External rule", "Import must not modify the existing Windows rule.");
        Check(WindowsFirewallSync.Plan(Profile(), null, [external], []).Links.Count == 0, "Unselected Windows rules must stay unlinked.");
        var importedState = new WindowsFirewallSyncState(profileId, import.Links);
        var updateImported = WindowsFirewallSync.Plan(Profile(import.Rules.Single() with { RemotePort = 8080 }), importedState, [external], []);
        Check(updateImported.Ready && updateImported.Changes.Single().After!.Description == "Original description" && updateImported.Changes.Single().After!.Group == "Third party", "Imported metadata must be preserved.");
        var unsupported = external with { Rule = null, Problem = "Service restriction", Fingerprint = "" };
        Check(!WindowsFirewallSync.Plan(Profile(), null, [unsupported], [unsupported]).Ready, "Unsupported Windows rules must not import.");
        Check(!WindowsFirewallSync.Plan(Profile(import.Rules.Single()), importedState, [unsupported], [], WindowsFirewallConflictChoice.ZikWall).Ready, "Resolution must never erase advanced Windows conditions.");
        Check(!WindowsFirewallSync.Plan(Profile(), null, [external, external], [external]).Ready, "Duplicate native names must be rejected.");
        var copiedMarker = native[0] with { Name = "Copied identity" };
        Check(!WindowsFirewallSync.Plan(Profile(rule), state, [.. native, copiedMarker], []).Ready, "Indexed lookup must retain duplicate markers.");
        var unrelatedHeader = native[0] with { Rule = null, Description = "Unrelated", Fingerprint = "", Problem = "Unlinked rule" };
        Check(!WindowsFirewallSync.Plan(Profile(rule), null, [unrelatedHeader], []).Ready, "Selective reads must retain unrelated name collisions.");
        var pollStore = new FakeStore(native.ToList());
        var poll = WindowsFirewallPolling.Plan(pollStore, Profile(rule), state);
        Check(poll.Ready && poll.Summary.Count == 0 && pollStore.LastSyncTargets?.Count == 2, "Polling must use the selective read seam with both directions.");
        pollStore.Entries = [native[0] with { Rule = null, Fingerprint = "", Problem = "New service restriction" }, native[1]];
        Check(!WindowsFirewallPolling.Plan(pollStore, Profile(rule), state).Ready, "Polling must inspect changed linked conditions afresh.");
        Check(!WindowsFirewallSync.Plan(Profile(rule with { Expires = DateTimeOffset.UtcNow.AddMinutes(5) }), null, [], []).Ready, "Expiry cannot become a permanent native rule.");
        var allow = rule with { Id = Guid.NewGuid(), Name = "Allow", Block = false, RemoteNetwork = "192.0.2.1" };
        Check(!WindowsFirewallSync.Plan(Profile(allow, rule), null, [], []).Ready, "Overlapping allow/block order semantics must not silently change.");
        Check(WindowsFirewallSync.Plan(Profile(allow with { RemoteNetwork = "198.51.100.1" }, rule), null, [], []).Ready, "Disjoint allow/block rules should sync.");
        Check(WindowsFirewallSync.Plan(Profile(allow with { Enabled = false }, rule), null, [], []).Ready, "Disabled conflicting rules should not prevent sync.");
        Reject(() => WindowsFirewallSync.Plan(Profile(rule) with { Id = Guid.NewGuid() }, state, native, []), "A second profile must not take ownership implicitly.");

        var fake = new FakeStore([external]);
        WindowsFirewallWriter.Apply(fake, new(initial.Changes));
        Check(fake.Entries.Count == 3 && fake.Entries.Contains(external), "Creating rules must preserve unrelated native entries.");
        int writes = fake.Writes;
        int reads = fake.Reads;
        WindowsFirewallWriter.Apply(fake, new(initial.Changes, true));
        Check(fake.Writes == writes, "Resume must be idempotent after a completed batch.");
        Check(fake.Reads - reads == 2, "A completed retry must use one preflight and one final inventory scan, independent of rule count.");
        var changingRetry = new FakeStore(native.ToList());
        changingRetry.BeforeRead = current => { if (current.Reads == 2) current.Entries[0] = Edit(current.Entries[0], current.Entries[0].Rule! with { RemotePort = 1234 }); };
        Reject(() => WindowsFirewallWriter.Apply(changingRetry, new(initial.Changes, true)), "A completed retry must still detect drift during final verification.");
        Check(changingRetry.Writes == 0, "Drift in an already completed retry must not trigger an unreviewed write.");
        WindowsFirewallWriter.Apply(fake, new(push.Changes));
        Check(fake.Entries.Count == 3 && fake.Entries.Where(x => x != external).All(x => !x.Rule!.Enabled), "Native edits must replace only expected rules.");
        var stale = new FakeStore([.. remoteEdit, external]);
        Reject(() => WindowsFirewallWriter.Apply(stale, new(push.Changes)), "Stale previews must not overwrite Windows edits.");
        Check(stale.Writes == 0, "Full batch preflight must happen before any write.");
        var duplicate = new FakeStore([.. native, native[0]]);
        Reject(() => WindowsFirewallWriter.Apply(duplicate, new(push.Changes)), "Ambiguous native names must not be removed.");
        Check(duplicate.Writes == 0, "Duplicate rejection must precede mutation.");
        var interruptedStore = new FakeStore(native.ToList()) { FailAdd = true };
        try { WindowsFirewallWriter.Apply(interruptedStore, new(push.Changes)); throw new Exception("Failure injection did not fire."); } catch (IOException) { checks++; }
        Check(interruptedStore.Entries.Count == 1, "Fixture should simulate a crash between remove and add.");
        interruptedStore.FailAdd = false;
        Reject(() => WindowsFirewallWriter.Apply(interruptedStore, new(push.Changes)), "A missing rule must require explicit journal resume.");
        WindowsFirewallWriter.Apply(interruptedStore, new(push.Changes, true));
        Check(interruptedStore.Entries.Count == 2 && interruptedStore.Entries.All(x => x.Rule!.RemotePort == 8443), "Journal resume must repair the remove/add crash window.");
        var tampered = native[0] with { Rule = native[0].Rule! with { RemotePort = 1234 } };
        Reject(() => WindowsFirewallWriter.Apply(new FakeStore([]), new([new(null, tampered)])), "A tampered snapshot must fail independent broker validation.");
        string directory = Path.Combine(Path.GetTempPath(), "ZikWall-sync-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var document = new PolicyDocument(1, profileId, false, [Profile(localEdit)]) { WindowsSync = state with { Automatic = true, Pending = new(push.Rules, push.Links, push.Changes) } };
            var store = new PolicyStore(directory); store.Save(document); var loaded = store.Load();
            Check(loaded.WindowsSync?.Pending?.Changes.Count == 2 && loaded.WindowsSync.Automatic, "Recovery journal and automation choice must survive restart.");
            using var stream = new MemoryStream();
            BrokerWire.WriteAsync(stream, new BrokerRequest("windows-sync", WindowsBatch: new(push.Changes)), CancellationToken.None).GetAwaiter().GetResult(); stream.Position = 0;
            Check(BrokerWire.ReadAsync<BrokerRequest>(stream, CancellationToken.None).GetAwaiter().GetResult().WindowsBatch?.Changes.Count == 2, "Native sync IPC must round-trip.");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine($"PASS: {checks} two-way Windows Firewall sync, conflict, persistence, and interrupted-write checks.");
    }

    public static void ReadNative()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var store = new WindowsFirewallStore();
        var entries = store.Read();
        Console.WriteLine($"Read-only Windows Firewall enumeration: {entries.Count} rules, {entries.Count(x => x.Problem.Length == 0)} compatible.");
        foreach (var reason in entries.Where(x => x.Problem.Length > 0).GroupBy(x => x.Problem)) Console.WriteLine($"  {reason.Count()}: {reason.Key}");
        foreach (var entry in entries.Where(x => x.Problem.Length == 0)) entry.ValidateWritable();
        var linked = entries.Where(x => x.Rule is not null).Take(3).ToList();
        var selective = store.ReadForSync(linked);
        if (selective.Count != entries.Count) throw new InvalidOperationException("Inventory changed during selective read parity check.");
        foreach (var entry in linked)
            if (!selective.Any(x => WindowsFirewallSync.Same(x, entry))) throw new InvalidOperationException("Selective read lost a linked rule or its conditions.");
        var linkedNames = linked.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selective.Any(x => x.Rule is not null && !linkedNames.Contains(x.Name))) throw new InvalidOperationException("Selective read inspected unlinked rule bodies.");
        Console.WriteLine("PASS: selective native reads retain inventory identities and complete linked-rule conditions.");
    }

    public static void NativeRoundTrip()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (!new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Run this disabled-rule diagnostic from an administrator terminal.");
        using var store = new WindowsFirewallStore();
        store.EnsureWritable();
        var id = Guid.NewGuid();
        var fixture = new FirewallRule(Guid.NewGuid(), "ZikWall disabled sync diagnostic", Environment.ProcessPath!, "127.0.0.1", 6, 59997, 3, true, false, null);
        var profile = new NetworkProfile(id, "Diagnostic", [fixture]);
        var initial = WindowsFirewallSync.Plan(profile, null, [], []);
        var names = initial.Links.SelectMany(x => x.Windows).Select(x => x.Name).ToHashSet();
        try
        {
            WindowsFirewallWriter.Apply(store, new(initial.Changes));
            var current = store.Read();
            var entries = current.Where(x => names.Contains(x.Name)).ToList();
            if (entries.Count != 2 || entries.Any(x => x.Rule is null || x.Rule.Enabled)) throw new Exception("Disabled native rule round trip failed.");
            var state = new WindowsFirewallSyncState(id, initial.Links);
            if (WindowsFirewallSync.Plan(profile, state, current, []).Summary.Count != 0) throw new Exception("Native canonicalization would cause a sync loop.");
            var edited = profile with { Rules = [fixture with { Protocol = 17, RemotePort = 59998, RemoteNetwork = "::1" }] };
            var update = WindowsFirewallSync.Plan(edited, state, current, []);
            if (!update.Ready) throw new Exception(string.Join("; ", update.Problems));
            WindowsFirewallWriter.Apply(store, new(update.Changes));
            current = store.Read();
            var changed = current.Where(x => names.Contains(x.Name)).ToList();
            if (changed.Any(x => x.Rule?.Protocol != 17 || x.Rule.RemoteNetwork != "::1/128" || x.Rule.Enabled)) throw new Exception("Native UDP/IPv6 update failed.");
            // Simulate an edit in Windows Firewall, then pull it through the planner.
            var remote = changed[0];
            store.Remove(remote.Name);
            store.Add(WindowsFirewallEntry.Create(remote.Name, remote.Description, remote.Group, remote.Rule! with { RemotePort = 59996 }));
            var conflict = WindowsFirewallSync.Plan(edited, new(id, update.Links), store.Read(), []);
            if (conflict.Ready) throw new Exception("Divergent native direction pairs must be detected.");
            var repair = WindowsFirewallSync.Plan(edited, new(id, update.Links), store.Read(), [], WindowsFirewallConflictChoice.ZikWall);
            WindowsFirewallWriter.Apply(store, new(repair.Changes));
            var any = edited with { Rules = [fixture with { Protocol = 0, RemotePort = 0, RemoteNetwork = "" }] };
            var anyPlan = WindowsFirewallSync.Plan(any, new(id, repair.Links), store.Read(), []);
            WindowsFirewallWriter.Apply(store, new(anyPlan.Changes));
            var delete = WindowsFirewallSync.Plan(any with { Rules = [] }, new(id, anyPlan.Links), store.Read(), []);
            WindowsFirewallWriter.Apply(store, new(delete.Changes));
            if (store.Read().Any(x => names.Contains(x.Name))) throw new Exception("Native deletion failed.");
            Console.WriteLine("PASS: native disabled-rule creation, TCP/UDP/any, IPv4/IPv6, both directions, canonicalization, external edit conflict, repair, and deletion. No test rule was enabled.");
        }
        finally
        {
            foreach (var entry in store.Read().Where(x => names.Contains(x.Name) && x.Description.StartsWith($"{WindowsFirewallSync.Marker}{id:N}:", StringComparison.Ordinal))) store.Remove(entry.Name);
        }
    }
}
