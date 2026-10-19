extern alias Workers;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using WorkerIngestion = Workers::CompareShopAPI.Workers.Ingestion;
using Xunit;

namespace CompareShopAPI.Tests;

public sealed class BhxForwardProxyTests
{
    [Theory]
    [InlineData("www.bachhoaxanh.com:443")]
    [InlineData("other.example:443")]
    [InlineData("bachhoaxanh.com.evil.example:443")]
    [InlineData("bachhoaxanh.com:444")]
    public async Task Proxy_denies_unapproved_CONNECT_targets_before_upstream_io(string authority)
    {
        var upstreamCalls = 0;
        await using var proxy = await WorkerIngestion.BhxForwardProxy.StartAsync((_, _, _) =>
        {
            Interlocked.Increment(ref upstreamCalls);
            throw new InvalidOperationException("Denied target reached upstream connector.");
        });

        var status = await SendProxyRequestAsync(proxy.Server, $"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 403 Forbidden", status, StringComparison.Ordinal);
        Assert.Equal(0, upstreamCalls);
    }

    [Fact]
    public async Task Proxy_opens_only_approved_upstream_for_allowed_CONNECT()
    {
        using var upstreamListener = new TcpListener(IPAddress.Loopback, 0);
        upstreamListener.Start();
        var upstreamEndpoint = (IPEndPoint)upstreamListener.LocalEndpoint;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var acceptedUpstream = upstreamListener.AcceptTcpClientAsync(cancellation.Token);
        var calls = new ConcurrentQueue<(string Host, int Port)>();
        await using var proxy = await WorkerIngestion.BhxForwardProxy.StartAsync(async (host, port, ct) =>
        {
            calls.Enqueue((host, port));
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, upstreamEndpoint.Port, ct);
            return client;
        }, cancellation.Token);

        var status = await SendProxyRequestAsync(proxy.Server, "CONNECT bachhoaxanh.com:443 HTTP/1.1\r\nHost: bachhoaxanh.com:443\r\n\r\n", cancellation.Token);
        using var accepted = await acceptedUpstream;

        Assert.StartsWith("HTTP/1.1 200 Connection Established", status, StringComparison.Ordinal);
        Assert.Equal((WorkerIngestion.BhxRequestGuard.AllowedHost, 443), Assert.Single(calls));
    }

    [Fact]
    public async Task Proxy_rejects_non_CONNECT_requests_without_opening_upstream()
    {
        var upstreamCalls = 0;
        await using var proxy = await WorkerIngestion.BhxForwardProxy.StartAsync((_, _, _) =>
        {
            Interlocked.Increment(ref upstreamCalls);
            throw new InvalidOperationException("Non-CONNECT request reached upstream connector.");
        });

        var status = await SendProxyRequestAsync(proxy.Server, "GET https://bachhoaxanh.com/bia HTTP/1.1\r\nHost: bachhoaxanh.com\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 405 Method Not Allowed", status, StringComparison.Ordinal);
        Assert.Equal(0, upstreamCalls);
    }

    [Fact]
    public async Task Proxy_upstream_failure_returns_gateway_error_without_direct_fallback()
    {
        var upstreamCalls = 0;
        await using var proxy = await WorkerIngestion.BhxForwardProxy.StartAsync((_, _, _) =>
        {
            Interlocked.Increment(ref upstreamCalls);
            throw new IOException("Approved upstream is unavailable.");
        });

        var status = await SendProxyRequestAsync(proxy.Server, "CONNECT bachhoaxanh.com:443 HTTP/1.1\r\nHost: bachhoaxanh.com:443\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 502 Bad Gateway", status, StringComparison.Ordinal);
        Assert.Equal(1, upstreamCalls);
    }

    [Fact]
    public void Browser_configuration_cannot_bypass_or_fall_back_from_loopback_proxy()
    {
        var options = WorkerIngestion.BhxBrowserLaunchOptions.Create("http://127.0.0.1:43127");

        Assert.Equal("http://127.0.0.1:43127", options.Proxy!.Server);
        Assert.Equal("<-loopback>", options.Proxy.Bypass);
        Assert.Contains("--disable-quic", options.Args!);
        Assert.Contains("--disable-webrtc", options.Args!);
        Assert.Contains("--force-webrtc-ip-handling-policy=disable_non_proxied_udp", options.Args!);
        Assert.DoesNotContain("DIRECT", options.Args!, StringComparer.OrdinalIgnoreCase);
        Assert.Throws<ArgumentException>(() => WorkerIngestion.BhxBrowserLaunchOptions.Create("http://proxy.example:43127"));
        Assert.Throws<ArgumentException>(() => WorkerIngestion.BhxBrowserLaunchOptions.Create(""));
    }

    [Fact]
    public void Browser_launch_does_not_override_playwright_user_agent()
    {
        var options = WorkerIngestion.BhxBrowserLaunchOptions.Create("http://127.0.0.1:43127");

        Assert.DoesNotContain(options.Args!, argument => argument.StartsWith("--user-agent", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string> SendProxyRequestAsync(string proxyServer, string request, CancellationToken ct = default)
    {
        var endpoint = new Uri(proxyServer);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Parse(endpoint.Host), endpoint.Port, ct);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), ct);
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        var status = await reader.ReadLineAsync(ct) ?? string.Empty;
        while (!string.IsNullOrEmpty(await reader.ReadLineAsync(ct))) { }
        return status;
    }
}
