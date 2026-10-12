using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Net;

namespace ZikWall.WinUI.Services;

public sealed class CountryDatabase : IDisposable
{
    private sealed class Table : IDisposable
    {
        private readonly MemoryMappedFile file;
        private readonly MemoryMappedViewAccessor view;
        private readonly int width, count;
        public Table(string path, int width)
        {
            this.width = width;
            long length = new FileInfo(path).Length;
            if (length is < 8 or > 80_000_000) throw new InvalidDataException("Invalid country table length.");
            file = MemoryMappedFile.CreateFromFile(path, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            try { view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read); }
            catch { file.Dispose(); throw; }
            if (view.ReadUInt32(0) != 0x3143575a || (count = view.ReadInt32(4)) < 0 || length != 8L + count * (width * 2L + 2))
            { Dispose(); throw new InvalidDataException("Invalid country table header."); }
        }
        private UInt128 Address(long offset)
        {
            if (width == 4) return System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(view.ReadUInt32(offset));
            ulong high = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(view.ReadUInt64(offset));
            ulong low = System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(view.ReadUInt64(offset + 8));
            return ((UInt128)high << 64) | low;
        }
        public string Find(byte[] bytes)
        {
            UInt128 key = 0; foreach (byte b in bytes) key = (key << 8) | b;
            int low = 0, high = count - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2; long offset = 8L + middle * (width * 2 + 2);
                if (key < Address(offset)) high = middle - 1;
                else if (key > Address(offset + width)) low = middle + 1;
                else return new string([(char)view.ReadByte(offset + width * 2), (char)view.ReadByte(offset + width * 2 + 1)]);
            }
            return "";
        }
        public void Dispose() { view.Dispose(); file.Dispose(); }
    }
    private readonly Table ipv4, ipv6;
    private readonly Dictionary<string, (string Code, string Name)> cache = new(StringComparer.Ordinal);
    public CountryDatabase(string directory)
    {
        ipv4 = new(Path.Combine(directory, "ipv4.zwc"), 4);
        try { ipv6 = new(Path.Combine(directory, "ipv6.zwc"), 16); }
        catch { ipv4.Dispose(); throw; }
    }
    public (string Code, string Name) Find(string address)
    {
        if (cache.TryGetValue(address, out var found)) return found;
        if (!IPAddress.TryParse(address, out var ip)) return ("", "");
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var bytes = ip.GetAddressBytes();
        string code = (bytes.Length == 4 ? ipv4 : ipv6).Find(bytes), name = code;
        if (code is "ZZ" or "XX") code = name = "";
        try { if (code.Length > 0) name = new RegionInfo(code).EnglishName; } catch (ArgumentException) { }
        if (cache.Count >= 4096) cache.Clear();
        return cache[address] = (code, name);
    }
    public void Dispose() { ipv4.Dispose(); ipv6.Dispose(); }
}
