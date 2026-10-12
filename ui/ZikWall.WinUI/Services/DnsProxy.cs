using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using ZikWall.WinUI.Models;

namespace ZikWall.WinUI.Services;

public static class DnsMessage
{
    public static (string Name, int QuestionEnd) Question(byte[] query)
    {
        if (query.Length is < 17 or > 65535 || (query[2] & 0xf8) != 0 || BinaryPrimitives.ReadUInt16BigEndian(query.AsSpan(4)) != 1) throw new InvalidDataException("Unsupported DNS query.");
        var labels = new List<string>(); int offset = 12, total = 0;
        while (true)
        {
            if (offset >= query.Length) throw new InvalidDataException("Truncated DNS name.");
            int size = query[offset++]; if (size == 0) break;
            if (size > 63 || offset + size > query.Length || (total += size + 1) > 254) throw new InvalidDataException("Invalid DNS label.");
            if (query.AsSpan(offset, size).ContainsAnyInRange((byte)128, (byte)255)) throw new InvalidDataException("Non-ASCII DNS label.");
            labels.Add(Encoding.ASCII.GetString(query, offset, size).ToLowerInvariant()); offset += size;
        }
        if (offset + 4 > query.Length) throw new InvalidDataException("Truncated DNS question.");
        return (string.Join('.', labels), offset + 4);
    }
    public static byte[] Error(byte[] query, int code)
    {
        var (_, end) = Question(query); var answer = query[..end];
        answer[2] = (byte)(0x80 | (query[2] & 1)); answer[3] = (byte)(0x80 | code);
        Array.Clear(answer, 6, 6); return answer;
    }
}

// Explicitly started loopback resolver. It does not change any adapter or system DNS setting.
public sealed class DnsProxy : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim capacity = new(32);
    private readonly HttpClient client;
    private readonly UdpClient udp;
    private readonly TcpListener tcp;
    private readonly Uri endpoint;
    private FrozenSet<string> blocked = Array.Empty<string>().ToFrozenSet();
    private FrozenSet<string> allowed = Array.Empty<string>().ToFrozenSet();
    private long queries, denied, errors;
    public int LocalPort => ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    public (long Queries, long Blocked, long Errors) Counts => (Interlocked.Read(ref queries), Interlocked.Read(ref denied), Interlocked.Read(ref errors));
    public DnsProxy(Uri endpoint, IPAddress bootstrap, int port = 53)
    {
        if (endpoint.Scheme != "https" || endpoint.UserInfo.Length > 0 || endpoint.Fragment.Length > 0) throw new ArgumentException("Use an HTTPS DNS endpoint.");
        this.endpoint = endpoint;
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectCallback = async (context, token) =>
        {
            var socket = new Socket(bootstrap.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(bootstrap, endpoint.Port), token); return new NetworkStream(socket, true); }
            catch { socket.Dispose(); throw; }
        } };
        client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        // HttpClient still validates the HTTPS certificate against the URI hostname.
        udp = new UdpClient(AddressFamily.InterNetwork);
        try { udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, port)); }
        catch { udp.Dispose(); client.Dispose(); throw; }
        tcp = new TcpListener(IPAddress.Loopback, LocalPort);
        try { tcp.Start(32); }
        catch { udp.Dispose(); tcp.Stop(); client.Dispose(); throw; }
        _ = ReceiveUdpAsync(); _ = ReceiveTcpAsync();
    }
    public void SetLists(IEnumerable<string> domains, IEnumerable<string> exceptions)
    {
        Volatile.Write(ref blocked, domains.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
        Volatile.Write(ref allowed, exceptions.ToFrozenSet(StringComparer.OrdinalIgnoreCase));
    }
    private async Task<byte[]> AnswerAsync(byte[] query, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5)); token = timeout.Token;
        var question = DnsMessage.Question(query); Interlocked.Increment(ref queries);
        if (BlocklistParser.MatchesDomain(question.Name, Volatile.Read(ref blocked), Volatile.Read(ref allowed))) { Interlocked.Increment(ref denied); return DnsMessage.Error(query, 3); }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new ByteArrayContent(query) };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message"); request.Headers.Accept.Add(new("application/dns-message"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentType?.MediaType != "application/dns-message") throw new InvalidDataException("Unexpected DNS response type.");
            using var stream = await response.Content.ReadAsStreamAsync(token);
            var answer = await BlocklistService.ReadBoundedAsync(stream, 65535, token);
            if (answer.Length < 12 || answer[0] != query[0] || answer[1] != query[1] || (answer[2] & 128) == 0) throw new InvalidDataException("Invalid upstream DNS response.");
            return answer;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        { Interlocked.Increment(ref errors); return DnsMessage.Error(query, 2); }
    }
    private async Task ReceiveUdpAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var received = await udp.ReceiveAsync(stop.Token);
                if (!capacity.Wait(0)) continue;
                _ = ServeUdpAsync(received);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }
    private async Task ServeUdpAsync(UdpReceiveResult received)
    {
        try
        {
            var answer = await AnswerAsync(received.Buffer, stop.Token);
            // Use TCP for answers larger than a conservative UDP payload instead of fragmenting.
            if (answer.Length > 512) { answer = DnsMessage.Error(received.Buffer, 0); answer[2] |= 2; }
            await udp.SendAsync(answer, received.RemoteEndPoint, stop.Token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { Interlocked.Increment(ref errors); }
        finally { capacity.Release(); }
    }
    private async Task ReceiveTcpAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var connection = await tcp.AcceptTcpClientAsync(stop.Token);
                if (!capacity.Wait(0)) { connection.Dispose(); continue; }
                _ = ServeTcpAsync(connection);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }
    private async Task ServeTcpAsync(TcpClient connection)
    {
        using (connection)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var stream = connection.GetStream(); var size = new byte[2]; await stream.ReadExactlyAsync(size, timeout.Token);
                var query = new byte[BinaryPrimitives.ReadUInt16BigEndian(size)]; await stream.ReadExactlyAsync(query, timeout.Token);
                var answer = await AnswerAsync(query, timeout.Token); BinaryPrimitives.WriteUInt16BigEndian(size, (ushort)answer.Length);
                await stream.WriteAsync(size, timeout.Token); await stream.WriteAsync(answer, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { Interlocked.Increment(ref errors); }
            finally { capacity.Release(); }
        }
    }
    public void Dispose() { stop.Cancel(); udp.Dispose(); tcp.Stop(); client.Dispose(); }
}
