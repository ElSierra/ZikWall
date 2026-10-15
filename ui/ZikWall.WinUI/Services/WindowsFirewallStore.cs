using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.CSharp.RuntimeBinder;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

public interface IWindowsFirewallStore
{
    List<WindowsFirewallEntry> Read();
    List<WindowsFirewallEntry> ReadNames(IReadOnlySet<string> names) => Read().Where(x => names.Contains(x.Name)).ToList();
    List<WindowsFirewallEntry> ReadForSync(IReadOnlyList<WindowsFirewallEntry> linked) => Read();
    void Remove(string name);
    void Add(WindowsFirewallEntry entry);
}

// No PowerShell, shell commands, registry writes, defaults, or firewall toggles.
// The elevated broker owns writes; the UI can enumerate this same adapter read-only.
[SupportedOSPlatform("windows")]
public sealed class WindowsFirewallStore : IWindowsFirewallStore, IDisposable
{
    [ComImport, Guid("9C4C6277-5027-441E-AFAE-CA1F542DA009"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetFwRules
    {
        int Count { get; }
        void Add([MarshalAs(UnmanagedType.Interface)] object rule);
        void Remove([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.Interface)] object Item([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNewEnum();
    }
    [ComImport, Guid("00020404-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumVariant
    {
        [PreserveSig] int Next(uint count, [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Struct, SizeParamIndex = 0)] object[] values, out uint fetched);
        [PreserveSig] int Skip(uint count);
        void Reset();
        void Clone(out IEnumVariant copy);
    }
    private readonly dynamic policy;
    private readonly dynamic rules;
    public WindowsFirewallStore()
    {
        policy = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2", true)!)!;
        try { rules = policy.Rules; }
        catch { Marshal.FinalReleaseComObject(policy); throw; }
    }
    public void EnsureWritable()
    {
        if ((int)policy.LocalPolicyModifyState != 0)
            throw new InvalidDataException("Windows policy prevents local firewall changes from taking effect. Review Group Policy before syncing.");
    }
    public List<WindowsFirewallEntry> Read() => ReadCore(null);
    public List<WindowsFirewallEntry> ReadNames(IReadOnlySet<string> names) => ReadCore(names);
    public List<WindowsFirewallEntry> ReadForSync(IReadOnlyList<WindowsFirewallEntry> linked) => ReadCore(null,
        linked.Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
        linked.Where(x => x.Description.StartsWith(WindowsFirewallSync.Marker, StringComparison.Ordinal)).Select(x => x.Description).ToHashSet(StringComparer.Ordinal));
    private List<WindowsFirewallEntry> ReadCore(IReadOnlySet<string>? names, IReadOnlySet<string>? linkedNames = null, IReadOnlySet<string>? linkedMarkers = null)
    {
        var result = new List<WindowsFirewallEntry>();
        object enumerator = ((INetFwRules)rules).GetNewEnum();
        try
        {
            var values = new object[1];
            while (true)
            {
                int status = ((IEnumVariant)enumerator).Next(1, values, out uint fetched);
                if (status < 0) Marshal.ThrowExceptionForHR(status);
                if (fetched == 0) break;
                try
                {
                    dynamic native = values[0];
                    string? name = null, description = null;
                    if (names is not null)
                    {
                        name = native.Name ?? "";
                        if (!names.Contains(name)) continue;
                    }
                    if (linkedNames is not null)
                    {
                        name ??= native.Name ?? "";
                        bool linkedName = linkedNames.Contains(name);
                        if (!linkedName && linkedMarkers!.Count > 0) description = native.Description ?? "";
                        if (!linkedName && (description is null || !linkedMarkers!.Contains(description)))
                        {
                            // Retain identities for collision detection. Inspect every condition
                            // of linked rules afresh, including renamed rules and copied markers.
                            result.Add(new(name, description ?? "", "", null, "", "Unlinked rule"));
                            if (result.Count > 20000) throw new InvalidDataException("Windows Firewall has too many rules to read safely.");
                            continue;
                        }
                    }
                    result.Add(ReadRule((object)native, name, description));
                }
                finally { Marshal.FinalReleaseComObject(values[0]); }
                if (result.Count > 20000) throw new InvalidDataException("Windows Firewall has too many rules to read safely.");
            }
        }
        finally { Marshal.FinalReleaseComObject(enumerator); }
        return result;
    }
    private static WindowsFirewallEntry ReadRule(dynamic native, string? name = null, string? description = null)
    {
        name ??= native.Name ?? ""; description ??= native.Description ?? "";
        string group = native.Grouping ?? "";
        try
        {
            int protocol = native.Protocol;
            if (protocol is not (6 or 17 or 256)) throw new InvalidDataException("Only TCP, UDP, and any-protocol rules can be linked.");
            if ((int)native.Profiles is not (7 or int.MaxValue)) throw new InvalidDataException("Restricted to specific Windows network profiles.");
            if (!string.IsNullOrEmpty((string?)native.ServiceName)) throw new InvalidDataException("Service-specific rule.");
            if ((string?)native.LocalAddresses is not (null or "" or "*")) throw new InvalidDataException("Local address restriction.");
            if (protocol != 256 && (string?)native.LocalPorts is not (null or "" or "*")) throw new InvalidDataException("Local port restriction.");
            if ((string?)native.InterfaceTypes is not (null or "" or "All")) throw new InvalidDataException("Interface type restriction.");
            object? interfaces = native.Interfaces;
            if (interfaces is not null && (interfaces is not Array array || array.Length != 0)) throw new InvalidDataException("Interface restriction.");
            // Read every Rule2/Rule3 condition. Failure to inspect a condition fails closed.
            if ((bool)native.EdgeTraversal || (int)native.EdgeTraversalOptions != 0) throw new InvalidDataException("Edge traversal rule.");
            if (!string.IsNullOrEmpty((string?)native.LocalAppPackageId)
                || !string.IsNullOrEmpty((string?)native.LocalUserOwner)
                || !string.IsNullOrEmpty((string?)native.LocalUserAuthorizedList)
                || !string.IsNullOrEmpty((string?)native.RemoteUserAuthorizedList)
                || !string.IsNullOrEmpty((string?)native.RemoteMachineAuthorizedList)
                || (int)native.SecureFlags != 0) throw new InvalidDataException("Package, user, machine, or IPsec restriction.");
            string ports = protocol == 256 ? "*" : native.RemotePorts ?? "*";
            int port = 0;
            if (ports is not ("" or "*") && (!int.TryParse(ports, out port) || port is < 1 or > 65535)) throw new InvalidDataException("Multiple ports, a range, or a port keyword.");
            int direction = native.Direction, action = native.Action;
            if (direction is not (1 or 2) || action is not (0 or 1)) throw new InvalidDataException("Unsupported direction or action.");
            var local = new FirewallRule(Guid.NewGuid(), WindowsFirewallSync.DisplayName(name, description),
                native.ApplicationName ?? "", WindowsFirewallSync.NormalizeNetwork(native.RemoteAddresses ?? "*"),
                protocol == 256 ? 0 : protocol, port, direction == 1 ? 2 : 1, action == 0, native.Enabled, null);
            local.Validate();
            return WindowsFirewallEntry.Create(name, description, group, local);
        }
        catch (Exception ex) when (ex is InvalidDataException or COMException or RuntimeBinderException)
        {
            return new(name, description, group, null, "", ex is InvalidDataException ? ex.Message : "Windows rule conditions could not be fully inspected.");
        }
    }
    public void Remove(string name) => rules.Remove(name);
    public void Add(WindowsFirewallEntry entry)
    {
        entry.ValidateWritable();
        var source = entry.Rule!;
        dynamic native = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule", true)!)!;
        try
        {
            native.Name = entry.Name;
            native.Description = entry.Description;
            native.Grouping = entry.Group;
            native.ApplicationName = source.Application;
            native.Protocol = source.Protocol == 0 ? 256 : source.Protocol;
            if (source.Protocol != 0) { native.LocalPorts = "*"; native.RemotePorts = source.RemotePort == 0 ? "*" : source.RemotePort.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            native.LocalAddresses = "*";
            native.RemoteAddresses = source.RemoteNetwork.Length == 0 ? "*" : source.RemoteNetwork;
            native.Direction = source.Direction == 1 ? 2 : 1;
            native.Action = source.Block ? 0 : 1;
            native.Profiles = int.MaxValue;
            native.InterfaceTypes = "All";
            native.EdgeTraversal = false;
            native.Enabled = source.Enabled;
            rules.Add(native);
        }
        finally { Marshal.FinalReleaseComObject(native); }
    }
    public void Dispose() { Marshal.FinalReleaseComObject(rules); Marshal.FinalReleaseComObject(policy); }
}

public static class WindowsFirewallWriter
{
    private static WindowsFirewallEntry? Named(List<WindowsFirewallEntry> rules, string name)
    {
        var matches = rules.Where(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1) throw new InvalidDataException($"{name}: duplicate Windows names; nothing with this name can be changed safely.");
        return matches.SingleOrDefault();
    }
    private static bool Check(WindowsFirewallChange change, List<WindowsFirewallEntry> current, bool resume)
    {
        var before = change.Before is null ? null : Named(current, change.Before.Name);
        var after = change.After is null ? null : Named(current, change.After.Name);
        bool sameName = string.Equals(change.Before?.Name, change.After?.Name, StringComparison.OrdinalIgnoreCase);
        if (change.After is null && before is null) return true;
        if (change.After is not null && WindowsFirewallSync.Same(after, change.After) && (sameName || before is null)) return true;
        if (!WindowsFirewallSync.Same(before, change.Before) && !(resume && before is null && change.Before is not null && change.After is not null))
            throw new InvalidDataException($"{change.Before?.Name ?? change.After!.Name}: changed since the preview. Refresh or resolve the pending sync before retrying.");
        if (!sameName && after is not null || sameName && after is not null && !WindowsFirewallSync.Same(after, change.Before))
            throw new InvalidDataException($"{change.After?.Name}: Windows rule name is already in use.");
        return false;
    }
    public static void Apply(IWindowsFirewallStore store, WindowsFirewallBatch batch)
    {
        WindowsFirewallSync.ValidateBatch(batch);
        // Preflight the entire batch before the first change, then check again at
        // each write to catch changes made while the preview was open.
        var names = batch.Changes.SelectMany(x => new[] { x.Before?.Name, x.After?.Name }).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshot = store.ReadNames(names);
        var pending = new List<WindowsFirewallChange>();
        foreach (var change in batch.Changes)
            if (!Check(change, snapshot, batch.Resume)) pending.Add(change);
        foreach (var change in pending)
        {
            snapshot = store.ReadNames(new[] { change.Before?.Name, change.After?.Name }.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (Check(change, snapshot, batch.Resume)) continue;
            if (change.Before is { } before && Named(snapshot, before.Name) is not null) store.Remove(before.Name);
            if (change.After is { } after) store.Add(after);
        }
        snapshot = store.ReadNames(names);
        foreach (var change in batch.Changes)
        {
            if (!Check(change, snapshot, false)) throw new IOException("Windows did not retain the requested rule. Resume sync to inspect the result.");
        }
    }
}
