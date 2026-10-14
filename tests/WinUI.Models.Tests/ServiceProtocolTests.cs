using System.Buffers.Binary;
using ZikWall.WinUI.Services;

internal static class ServiceProtocolTests
{
    public static async Task RejectUntrustedClientAsync()
    {
        // An unauthorized client must not stop the listener by sending a malformed
        // acknowledgment of its rejection. The next connection must still work.
        await RejectAsync(malformedAcknowledgment: true);
        await RejectAsync(malformedAcknowledgment: false);
        Console.WriteLine("PASS: the service rejected an untrusted executable and survived its malformed acknowledgment.");
    }

    private static async Task RejectAsync(bool malformedAcknowledgment)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = await ServiceSessionClient.ConnectAsync(timeout.Token);
        await ServiceProtocol.WriteAsync(pipe, new ServiceSessionRequest(1, "firewall"), timeout.Token);
        var result = await ServiceProtocol.ReadAsync<ServiceSessionResponse>(pipe, timeout.Token);
        if (malformedAcknowledgment) await ServiceProtocol.WriteAsync(pipe, "not-a-boolean", timeout.Token);
        else await ServiceProtocol.WriteAsync(pipe, true, timeout.Token);
        if (result.Success || result.ProcessId != 0 || result.PipeId.Length != 0)
            throw new Exception("The service authorized an executable other than the installed UI.");
    }

    public static async Task RunAsync()
    {
        void Check(bool value) { if (!value) throw new Exception("Service protocol validation failed."); }
        Check(ServiceProtocol.IsValid(new(1, "domains")) && ServiceProtocol.IsValid(new(1, "firewall")));
        foreach (var request in new[] { new ServiceSessionRequest(0, "domains"), new(2, "firewall"), new(1, "run"), new(1, ""), new(1, "C:\\cmd.exe") })
            Check(!ServiceProtocol.IsValid(request));
        using var stream = new MemoryStream();
        var response = new ServiceSessionResponse(true, "Session started", Guid.NewGuid().ToString("N"), 123);
        await ServiceProtocol.WriteAsync(stream, response, CancellationToken.None);
        stream.Position = 0;
        Check(await ServiceProtocol.ReadAsync<ServiceSessionResponse>(stream, CancellationToken.None) == response);
        foreach (int size in new[] { -1, 0, ServiceProtocol.MaximumBytes + 1, int.MaxValue })
        {
            byte[] header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, size);
            using var invalid = new MemoryStream(header);
            try { await ServiceProtocol.ReadAsync<ServiceSessionRequest>(invalid, CancellationToken.None); throw new Exception("Invalid frame accepted."); }
            catch (InvalidDataException) { }
        }
        using var truncated = new MemoryStream(new byte[] { 10, 0, 0, 0, 123 });
        try { await ServiceProtocol.ReadAsync<ServiceSessionRequest>(truncated, CancellationToken.None); throw new Exception("Truncated frame accepted."); }
        catch (EndOfStreamException) { }
        try { await ServiceProtocol.WriteAsync(stream, new string('x', ServiceProtocol.MaximumBytes), CancellationToken.None); throw new Exception("Oversized write accepted."); }
        catch (InvalidDataException) { }
        Console.WriteLine("PASS: service session allowlist, protocol version, round-trip, malformed/truncated frames and oversized messages.");
    }
}
