using System.Net;

namespace ZikWall.WinUI.Models;

public static class NetworkScope
{
    public static string Classify(string address)
    {
        if (!IPAddress.TryParse(address, out var ip)) return "Unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return "Loopback";
        var b = ip.GetAddressBytes();
        if (b.Length == 4)
        {
            if (b[0] == 0 || b[0] >= 224 || (b[0] == 192 && b[1] == 0 && b[2] == 2) || (b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100))) || (b[0] == 203 && b[1] == 0 && b[2] == 113)) return "Reserved";
            if (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] is >= 64 and <= 127)) return "Local network";
        }
        else
        {
            if (ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6Multicast || (b[0] == 0x20 && b[1] == 1 && b[2] == 0x0d && b[3] == 0xb8)) return "Reserved";
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xfe) == 0xfc) return "Local network";
            // Only currently allocated global-unicast space is an Internet candidate.
            if ((b[0] & 0xe0) != 0x20 || (b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xf0) == 0)) return "Reserved";
        }
        return "Internet";
    }
}
