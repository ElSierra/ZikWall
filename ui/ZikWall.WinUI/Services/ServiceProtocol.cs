using System.Buffers.Binary;
using System.Text.Json;

namespace ZikWall.WinUI.Services;

// Only a session type crosses the service boundary. Paths, process IDs and
// command lines are always obtained/constructed by the service itself.
public sealed record ServiceSessionRequest(int Version, string Mode);
public sealed record ServiceSessionResponse(bool Success, string Message, string PipeId = "", int ProcessId = 0);

public static class ServiceProtocol
{
    public const string Name = "ZikWallService";
    public const string PipeName = "ZikWall.Service.v1";
    public const int MaximumBytes = 4096;

    public static bool IsValid(ServiceSessionRequest request) => request.Version == 1 && request.Mode is "domains" or "firewall";

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Service message is too large.");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (size is < 1 or > MaximumBytes) throw new InvalidDataException("Invalid service message size.");
        byte[] bytes = new byte[size];
        await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("Empty service message.");
    }
}
