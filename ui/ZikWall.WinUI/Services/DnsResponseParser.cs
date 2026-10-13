using System.Buffers.Binary;
using System.Net;
using System.Text;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

// Untrusted wire data: bounded DNS names, backward-only compression pointers,
// answer-section records only, and addresses reachable from the question's CNAME chain.
internal static class DnsResponseParser
{
    private sealed record Record(string Owner, ushort Type, uint Ttl, string Value);
    internal static IReadOnlyList<CachedDomainAnswer> Parse(ReadOnlySpan<byte> message)
    {
        var result = new List<CachedDomainAnswer>();
        if (message.Length is < 12 or > 65535 || (U16(message, 2) & 0xfa0f) != 0x8000 || U16(message, 4) != 1) return result;
        int offset = 12, count = U16(message, 6);
        if (count is 0 or > 128 || !Name(message, ref offset, out string question) || offset + 4 > message.Length) return result;
        ushort questionType = U16(message, offset), questionClass = U16(message, offset + 2); offset += 4;
        if (questionType is not (1 or 28) || questionClass != 1 || !CachedDomainHints.ValidName(question)) return result;
        var records = new List<Record>();
        for (int i = 0; i < count; i++)
        {
            if (!Name(message, ref offset, out string owner) || offset + 10 > message.Length) return [];
            ushort type = U16(message, offset), cls = U16(message, offset + 2), length = U16(message, offset + 8);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(message.Slice(offset + 4, 4)); offset += 10;
            int end = offset + length;
            if (end > message.Length) return [];
            if (cls == 1 && CachedDomainHints.ValidName(owner))
            {
                if (type == 5)
                {
                    int cursor = offset;
                    if (!Name(message, ref cursor, out string alias) || cursor != end || !CachedDomainHints.ValidName(alias)) return [];
                    records.Add(new(owner, type, ttl, alias));
                }
                else if ((type == 1 && length == 4) || (type == 28 && length == 16))
                    records.Add(new(owner, type, ttl, new IPAddress(message.Slice(offset, length)).ToString()));
            }
            offset = end;
        }
        string current = question; uint chainTtl = uint.MaxValue;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (int depth = 0; depth < 16 && visited.Add(current); depth++)
        {
            var aliases = records.Where(r => r.Owner == current && r.Type == 5).ToArray();
            if (aliases.Length > 1 && aliases.Any(r => r.Value != aliases[0].Value)) return [];
            if (aliases.Length > 0)
            {
                chainTtl = Math.Min(chainTtl, aliases.Min(r => r.Ttl)); current = aliases[0].Value; continue;
            }
            foreach (var record in records.Where(r => r.Owner == current && r.Type == questionType))
                result.Add(new(record.Value, question, Math.Min(chainTtl, record.Ttl), true));
            break;
        }
        return result;
    }

    private static ushort U16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2));
    private static bool Name(ReadOnlySpan<byte> data, ref int offset, out string name)
    {
        name = ""; int cursor = offset, resume = -1, total = 0, jumps = 0;
        var labels = new List<string>();
        while (cursor < data.Length)
        {
            int position = cursor, size = data[cursor++];
            if (size == 0) { offset = resume < 0 ? cursor : resume; name = string.Join('.', labels); return true; }
            if ((size & 0xc0) == 0xc0)
            {
                if (cursor >= data.Length || ++jumps > 32) return false;
                int target = ((size & 0x3f) << 8) | data[cursor++];
                if (target < 12 || target >= position) return false;
                if (resume < 0) resume = cursor;
                cursor = target; continue;
            }
            if (size > 63 || cursor + size > data.Length || (total += size + 1) > 254) return false;
            var label = data.Slice(cursor, size);
            foreach (byte b in label) if (!(b is >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'0' and <= (byte)'9' or (byte)'-')) return false;
            labels.Add(Encoding.ASCII.GetString(label).ToLowerInvariant()); cursor += size;
        }
        return false;
    }
}
