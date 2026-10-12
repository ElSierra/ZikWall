using System.Buffers.Binary;
using System.Text.Json;

namespace ZikWall.WinUI.Services;

public static class BrokerWire
{
    public const int MaximumBytes = 2 * 1024 * 1024;
    public static void ValidateSize<T>(T value)
    {
        if (JsonSerializer.SerializeToUtf8Bytes(value).Length > MaximumBytes) throw new InvalidDataException("The policy exceeds the IPC message limit. Reduce the selected profile's rule count.");
    }
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The policy exceeds the IPC message limit.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token); await stream.FlushAsync(token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > MaximumBytes) throw new InvalidDataException("Invalid IPC message size.");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty IPC message.");
    }
}
