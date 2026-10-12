using System.Runtime.InteropServices;

namespace ZikWall.WinUI.Services;

public sealed record ConnectedNetwork(string Id, string Name);
public static class NetworkIdentity
{
    // GetNetworkId returns a GUID, which IDispatch's VARIANT marshaller cannot represent.
    // Use the documented dual-interface vtable for this call (netlistmgr.h).
    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
    }
    public static IReadOnlyList<ConnectedNetwork> Read()
    {
        if (!OperatingSystem.IsWindows()) return [];
        var result = new List<ConnectedNetwork>();
        object? manager = null, networks = null;
        try
        {
            manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"), true)!);
            networks = ((dynamic)manager!).GetNetworks(1); // NLM_ENUM_NETWORK_CONNECTED
            foreach (object network in (System.Collections.IEnumerable)networks)
            {
                try { var item = (INetwork)network; result.Add(new(item.GetNetworkId().ToString("D"), item.GetName())); }
                finally { Marshal.FinalReleaseComObject(network); }
            }
        }
        finally { if (networks is not null) Marshal.FinalReleaseComObject(networks); if (manager is not null) Marshal.FinalReleaseComObject(manager); }
        return result;
    }
}
