using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace CompareShopAPI.Workers.Ingestion;

public sealed class BhxForwardProxy : IAsyncDisposable
{
    private const int MaxHeaderBytes = 8192;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(8);
    private readonly TcpListener listener;
    private readonly CancellationTokenSource shutdown = new();
    private readonly Func<string, int, CancellationToken, Task<TcpClient>> connectUpstream;
    private readonly IReadOnlySet<string> allowedHosts;
    private readonly ConcurrentBag<Task> connectionTasks = [];
    private readonly SemaphoreSlim connectionSlots = new(64, 64);
    private Task? acceptTask;

    private BhxForwardProxy(TcpListener listener, Func<string, int, CancellationToken, Task<TcpClient>> connectUpstream, IReadOnlySet<string> allowedHosts)
    {
        this.listener = listener;
        this.connectUpstream = connectUpstream;
        this.allowedHosts = allowedHosts;
        Server = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
    }

    public string Server { get; }

    public static Task<BhxForwardProxy> StartAsync(CancellationToken ct = default) => StartAsync([BhxRequestGuard.AllowedHost], ct);
    public static Task<BhxForwardProxy> StartAsync(IEnumerable<string> hosts, CancellationToken ct = default) => StartAsync(ConnectUpstreamAsync, hosts, ct);

    internal static Task<BhxForwardProxy> StartAsync(Func<string, int, CancellationToken, Task<TcpClient>> connectUpstream, CancellationToken ct = default) => StartAsync(connectUpstream, [BhxRequestGuard.AllowedHost], ct);
    internal static Task<BhxForwardProxy> StartAsync(Func<string, int, CancellationToken, Task<TcpClient>> connectUpstream, IEnumerable<string> hosts, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(64);
        var allowedHosts = hosts.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (allowedHosts.Count == 0 || allowedHosts.Any(host => string.IsNullOrWhiteSpace(host) || host.Contains('/'))) { listener.Stop(); throw new ArgumentException("Exact allowed hosts are required.", nameof(hosts)); }
        var proxy = new BhxForwardProxy(listener, connectUpstream, allowedHosts);
        proxy.acceptTask = proxy.AcceptLoopAsync(proxy.shutdown.Token);
        return Task.FromResult(proxy);
    }

    public async ValueTask DisposeAsync()
    {
        shutdown.Cancel();
        listener.Stop();
        if (acceptTask is not null)
        {
            try { await acceptTask; }
            catch (OperationCanceledException) { }
            catch (SocketException) when (shutdown.IsCancellationRequested) { }
        }

        try { await Task.WhenAll(connectionTasks.ToArray()); }
        catch (OperationCanceledException) { }
        connectionSlots.Dispose();
        shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            try { await connectionSlots.WaitAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { client.Dispose(); break; }
            connectionTasks.Add(HandleClientAndReleaseAsync(client, ct));
        }
    }

    private async Task HandleClientAndReleaseAsync(TcpClient client, CancellationToken ct)
    {
        try { await HandleClientAsync(client, ct); }
        finally { connectionSlots.Release(); }
    }

    private async Task HandleClientAsync(TcpClient downstream, CancellationToken ct)
    {
        using (downstream)
        {
            try
            {
                var stream = downstream.GetStream();
                using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                handshake.CancelAfter(HandshakeTimeout);
                var handshakeToken = handshake.Token;
                var (requestLine, consumed) = await ReadLineAsync(stream, MaxHeaderBytes, handshakeToken);
                if (requestLine is null)
                {
                    await WriteStatusAsync(stream, "400 Bad Request", ct);
                    return;
                }

                var fields = requestLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 3 || fields[0] != "CONNECT")
                {
                    await WriteStatusAsync(stream, "405 Method Not Allowed", ct);
                    return;
                }

                if (!TryGetAllowedTarget(fields[1], allowedHosts, out var host))
                {
                    await WriteStatusAsync(stream, "403 Forbidden", ct);
                    return;
                }

                if (!await DrainHeadersAsync(stream, consumed, handshakeToken))
                {
                    await WriteStatusAsync(stream, "400 Bad Request", ct);
                    return;
                }

                TcpClient upstream;
                try { upstream = await connectUpstream(host, 443, handshakeToken); }
                catch (Exception ex) when (ex is SocketException or IOException or InvalidOperationException)
                {
                    await WriteStatusAsync(stream, "502 Bad Gateway", ct);
                    return;
                }

                using (upstream)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"), ct);
                    await TunnelAsync(stream, upstream.GetStream(), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (InvalidOperationException) { }
        }
    }

    private static bool TryGetAllowedTarget(string authority, IReadOnlySet<string> allowedHosts, out string host)
    {
        host = string.Empty;
        if (!Uri.TryCreate($"https://{authority}", UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Port != 443 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !allowedHosts.Contains(uri.IdnHost) || !string.Equals(authority, $"{uri.IdnHost}:443", StringComparison.OrdinalIgnoreCase))
            return false;

        host = uri.IdnHost;
        return true;
    }

    private static async Task<(string? Line, int Consumed)> ReadLineAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var buffer = new byte[1];
        while (bytes.Count < maxBytes)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0) return (null, bytes.Count);
            if (buffer[0] == (byte)'\n')
            {
                var consumed = bytes.Count + 1;
                if (bytes.Count > 0 && bytes[^1] == (byte)'\r') bytes.RemoveAt(bytes.Count - 1);
                return (Encoding.ASCII.GetString(bytes.ToArray()), consumed);
            }
            bytes.Add(buffer[0]);
        }
        return (null, maxBytes);
    }

    private static async Task<bool> DrainHeadersAsync(Stream stream, int consumed, CancellationToken ct)
    {
        while (consumed < MaxHeaderBytes)
        {
            var (line, bytes) = await ReadLineAsync(stream, MaxHeaderBytes - consumed, ct);
            if (line is null) return false;
            consumed += bytes;
            if (line.Length == 0) return true;
            if (!line.Contains(':', StringComparison.Ordinal)) return false;
        }
        return false;
    }

    private static Task WriteStatusAsync(Stream stream, string status, CancellationToken ct) =>
        stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\nContent-Length: 0\r\n\r\n"), ct).AsTask();

    private static async Task TunnelAsync(Stream downstream, Stream upstream, CancellationToken ct)
    {
        using var tunnel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var downToUp = downstream.CopyToAsync(upstream, 81920, tunnel.Token);
        var upToDown = upstream.CopyToAsync(downstream, 81920, tunnel.Token);
        await Task.WhenAny(downToUp, upToDown);
        tunnel.Cancel();
        try { await Task.WhenAll(downToUp, upToDown); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private static async Task<TcpClient> ConnectUpstreamAsync(string host, int port, CancellationToken ct)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, ct);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
