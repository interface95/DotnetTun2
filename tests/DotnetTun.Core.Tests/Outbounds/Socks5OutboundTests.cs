using System.Net;
using System.Net.Sockets;
using DotnetTun.Abstractions;
using DotnetTun.Outbounds.Socks5;
using Xunit;

namespace DotnetTun.Core.Tests.Outbounds;

public sealed class Socks5OutboundTests
{
    [Fact]
    public void Name_ReflectsOptionsName()
    {
        // Arrange
        IOutbound outbound = new Socks5Outbound(new Socks5OutboundOptions("127.0.0.1", 1080, name: "my-socks"));

        // Act / Assert
        Assert.Equal("my-socks", outbound.Name);
    }

    [Fact]
    public async Task ConnectAsync_WhenSocksServerStallsGreeting_ThrowsTimeout()
    {
        // Arrange
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await StalledTcpServer.StartAsync(cancellationToken);
        IOutbound outbound = new Socks5Outbound(new Socks5OutboundOptions(
            "127.0.0.1",
            server.Port,
            HandshakeTimeout: TimeSpan.FromMilliseconds(100)));

        // Act / Assert
        await Assert.ThrowsAsync<TimeoutException>(() => outbound.ConnectAsync("example.com", 443, cancellationToken).AsTask());
    }

    [Fact]
    public async Task ConnectAsync_WhenCallerCancelsDuringHandshake_ThrowsOperationCanceledException()
    {
        // Arrange
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await using var server = await StalledTcpServer.StartAsync(cancellationToken);
        IOutbound outbound = new Socks5Outbound(new Socks5OutboundOptions(
            "127.0.0.1",
            server.Port,
            HandshakeTimeout: TimeSpan.FromSeconds(10)));
        using var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        callerCancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        // Act / Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() => outbound.ConnectAsync("example.com", 443, callerCancellation.Token).AsTask());
    }

    [Fact]
    public async Task ConnectAsync_WithDomainTarget_PerformsSocks5ConnectAndReturnsConnectedStream()
    {
        // Arrange
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start();
        int socksPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        Task serverTask = RunFakeSocks5ServerAsync(listener, "api.anthropic.com", 443, cancellationToken);

        IOutbound outbound = new Socks5Outbound(new Socks5OutboundOptions("127.0.0.1", socksPort));

        // Act
        await using Stream stream = await outbound.ConnectAsync("api.anthropic.com", 443, cancellationToken);
        await stream.WriteAsync(new byte[] { 0x42 }, cancellationToken);
        byte[] response = new byte[1];
        int bytesRead = await stream.ReadAsync(response, cancellationToken);
        await serverTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);

        // Assert
        Assert.Equal(1, bytesRead);
        Assert.Equal(0x24, response[0]);
    }

    private static async Task RunFakeSocks5ServerAsync(TcpListener listener, string expectedHost, int expectedPort, CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = client.GetStream();

        byte[] greeting = await ReadExactAsync(stream, 3, cancellationToken);
        Assert.Equal([0x05, 0x01, 0x00], greeting);
        await stream.WriteAsync(new byte[] { 0x05, 0x00 }, cancellationToken);

        byte[] requestPrefix = await ReadExactAsync(stream, 5, cancellationToken);
        Assert.Equal(0x05, requestPrefix[0]);
        Assert.Equal(0x01, requestPrefix[1]);
        Assert.Equal(0x00, requestPrefix[2]);
        Assert.Equal(0x03, requestPrefix[3]);

        int hostLength = requestPrefix[4];
        byte[] hostBytes = await ReadExactAsync(stream, hostLength, cancellationToken);
        byte[] portBytes = await ReadExactAsync(stream, 2, cancellationToken);
        string host = System.Text.Encoding.ASCII.GetString(hostBytes);
        int port = (portBytes[0] << 8) | portBytes[1];
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);

        await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0x1F, 0x90 }, cancellationToken);

        byte[] payload = await ReadExactAsync(stream, 1, cancellationToken);
        Assert.Equal(0x42, payload[0]);
        await stream.WriteAsync(new byte[] { 0x24 }, cancellationToken);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        return buffer;
    }

    private sealed class StalledTcpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopSource;
        private readonly Task _acceptTask;
        private TcpClient? _client;

        private StalledTcpServer(TcpListener listener, CancellationTokenSource stopSource)
        {
            _listener = listener;
            _stopSource = stopSource;
            _acceptTask = AcceptAndStallAsync(_stopSource.Token);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static Task<StalledTcpServer> StartAsync(CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, port: 0);
            listener.Start();
            var stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return Task.FromResult(new StalledTcpServer(listener, stopSource));
        }

        public async ValueTask DisposeAsync()
        {
            await _stopSource.CancelAsync();
            _client?.Dispose();
            _listener.Stop();

            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Assert.True(_stopSource.IsCancellationRequested);
            }
            catch (ObjectDisposedException)
            {
                Assert.True(_stopSource.IsCancellationRequested);
            }

            _stopSource.Dispose();
        }

        private async Task AcceptAndStallAsync(CancellationToken cancellationToken)
        {
            _client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            await using NetworkStream stream = _client.GetStream();
            byte[] greeting = new byte[3];
            await stream.ReadExactlyAsync(greeting, cancellationToken).ConfigureAwait(false);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }
    }
}
