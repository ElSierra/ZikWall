using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Text;

namespace ZikWall.WinUI.Services;

// Offline IP → network owner (ASN) lookup over tables built by tools/build-network-data.py.
public sealed class NetworkDatabase : IDisposable
{
    private sealed class Mapped : IDisposable
    {
        private readonly MemoryMappedFile file;
        public readonly MemoryMappedViewAccessor View;
        public readonly long Length;
        public Mapped(string path, uint magic)
        {
            Length = new FileInfo(path).Length;
            if (Length is < 8 or > 120_000_000) throw new InvalidDataException("Invalid network table length.");
            file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            try { View = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read); }
            catch { file.Dispose(); throw; }
            if (View.ReadUInt32(0) != magic) { Dispose(); throw new InvalidDataException("Invalid network table header."); }
        }
        public void Dispose() { View.Dispose(); file.Dispose(); }
    }
    private sealed class Ranges : IDisposable
    {
        private readonly Mapped table;
        private readonly int width, count, row;
        public Ranges(string path, int width)
        {
            table = new(path, 0x314E575a);
            this.width = width; row = width * 2 + 4; count = table.View.ReadInt32(4);
            if (count < 0 || table.Length != 8L + (long)count * row) { table.Dispose(); throw new InvalidDataException("Invalid network table size."); }
        }
        private UInt128 Address(long offset)
        {
            if (width == 4) return BinaryPrimitives.ReverseEndianness(table.View.ReadUInt32(offset));
            ulong high = BinaryPrimitives.ReverseEndianness(table.View.ReadUInt64(offset)), low = BinaryPrimitives.ReverseEndianness(table.View.ReadUInt64(offset + 8));
            return ((UInt128)high << 64) | low;
        }
        public uint Find(byte[] bytes)
        {
            UInt128 key = 0; foreach (byte b in bytes) key = (key << 8) | b;
            int low = 0, high = count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2; long offset = 8L + (long)middle * row;
                if (key < Address(offset)) high = middle - 1;
                else if (key > Address(offset + width)) low = middle + 1;
                else return table.View.ReadUInt32(offset + width * 2);
            }
            return 0;
        }
        public void Dispose() => table.Dispose();
    }
    private sealed class Names : IDisposable
    {
        private readonly Mapped table;
        private readonly int count;
        private readonly long blob;
        private readonly uint blobLength;
        public Names(string path)
        {
            table = new(path, 0x314F575a);
            count = table.View.ReadInt32(4); blob = 8L + count * 8L + 4;
            if (count < 0 || blob > table.Length || (blobLength = table.View.ReadUInt32(blob - 4)) != table.Length - blob)
            { table.Dispose(); throw new InvalidDataException("Invalid network name table."); }
        }
        public string Find(uint asn)
        {
            int low = 0, high = count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2; uint found = table.View.ReadUInt32(8L + middle * 8L);
                if (asn < found) high = middle - 1;
                else if (asn > found) low = middle + 1;
                else
                {
                    uint start = table.View.ReadUInt32(12L + middle * 8L), end = middle + 1 < count ? table.View.ReadUInt32(12L + (middle + 1) * 8L) : blobLength;
                    if (start > end || end > blobLength || end - start > 1024) return "";
                    var bytes = new byte[end - start];
                    table.View.ReadArray(blob + start, bytes, 0, bytes.Length);
                    return Encoding.UTF8.GetString(bytes);
                }
            }
            return "";
        }
        public void Dispose() => table.Dispose();
    }
    private readonly Ranges ipv4, ipv6;
    private readonly Names names;
    private readonly Dictionary<string, (uint Asn, string Name)> cache = new(StringComparer.Ordinal);
    public NetworkDatabase(string directory)
    {
        var opened = new List<IDisposable>();
        try
        {
            opened.Add(ipv4 = new(Path.Combine(directory, "ipv4.zwn"), 4));
            opened.Add(ipv6 = new(Path.Combine(directory, "ipv6.zwn"), 16));
            names = new(Path.Combine(directory, "names.zwn"));
        }
        catch { foreach (var item in opened) item.Dispose(); throw; }
    }
    public (uint Asn, string Name) Find(string address)
    {
        if (cache.TryGetValue(address, out var found)) return found;
        if (!IPAddress.TryParse(address, out var ip)) return (0, "");
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        uint asn = (bytes.Length == 4 ? ipv4 : ipv6).Find(bytes);
        if (cache.Count >= 4096) cache.Clear();
        return cache[address] = (asn, asn == 0 ? "" : names.Find(asn));
    }
    public void Dispose() { ipv4.Dispose(); ipv6.Dispose(); names.Dispose(); }
}
