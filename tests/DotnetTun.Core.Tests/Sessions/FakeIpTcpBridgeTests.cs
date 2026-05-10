using System.Net;
using System.Net.Sockets;
using DotnetTun.Abstractions;
using DotnetTun.Core.Dns;
using DotnetTun.Core.Sessions;
using Xunit;

namespace DotnetTun.Core.Tests.Sessions;

public sealed class FakeIpTcpBridgeTests
{
    [Fact]
    public async Task Server_WithKnownFakeIp_AcceptsLocalConnectionAndRelaysThroughOutbound()
    {
        // Arrange
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");

        using var remoteListener = new TcpListener(IPAddress.Loopback, port: 0);
        remoteListener.Start();
        int remotePort = ((IPEndPoint)remoteListener.LocalEndpoint).Port;
        var outbound = new LoopbackOutbound(remotePort);
        var bridge = new FakeIpTcpBridge(pool, outbound);
        await using var server = new FakeIpTcpBridgeServer(bridge, IPAddress.Loopback, port: 0, lease.FakeIp, targetPort: 443);
        await server.StartAsync(cancellationToken);

        Task remoteTask = RunRemotePeerAsync(remoteListener, cancellationToken);

        using var appClient = new TcpClient();

        // Act
        await appClient.ConnectAsync(IPAddress.Loopback, server.Port, cancellationToken);
        await using NetworkStream appStream = appClient.GetStream();
        await appStream.WriteAsync(new byte[] { 0x42 }, cancellationToken);
        byte[] response = new byte[1];
        int bytesRead = await appStream.ReadAsync(response, cancellationToken);
        appClient.Close();
        await remoteTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);

        // Assert
        Assert.Equal("api.anthropic.com", outbound.Host);
        Assert.Equal(443, outbound.Port);
        Assert.Equal(1, bytesRead);
        Assert.Equal(0x24, response[0]);
    }

    [Fact]
    public async Task Server_WhenActiveConnectionLimitReached_ClosesSecondConnection()
    {
        // Arrange
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");
        var outbound = new BlockingOutbound();
        var bridge = new FakeIpTcpBridge(pool, outbound);
        await using var server = new FakeIpTcpBridgeServer(
            bridge,
            IPAddress.Loopback,
            port: 0,
            lease.FakeIp,
            targetPort: 443,
            maxActiveConnections: 1);
        await server.StartAsync(cancellationToken);

        using var firstClient = new TcpClient();
        await firstClient.ConnectAsync(IPAddress.Loopback, server.Port, cancellationToken);
        await outbound.ConnectStarted.WaitAsync(cancellationToken);

        using var secondClient = new TcpClient();

        // Act
        await secondClient.ConnectAsync(IPAddress.Loopback, server.Port, cancellationToken);
        await using NetworkStream secondStream = secondClient.GetStream();
        byte[] buffer = new byte[1];
        int bytesRead = await secondStream.ReadAsync(buffer, cancellationToken).AsTask().WaitAsync(TimeSpan.FromMilliseconds(250), cancellationToken);

        // Assert
        Assert.Equal(0, bytesRead);

        outbound.CompleteAllReads();
        firstClient.Client.Shutdown(SocketShutdown.Send);
        await server.StopAsync(cancellationToken);
    }

    [Fact]
    public async Task StopAsync_WhenActiveConnectionIsCanceledByServerStop_DoesNotThrow()
    {
        // Arrange
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");
        var outbound = new ControlledCancellationOutbound();
        var bridge = new FakeIpTcpBridge(pool, outbound);
        await using var server = new FakeIpTcpBridgeServer(bridge, IPAddress.Loopback, port: 0, lease.FakeIp, targetPort: 443);
        await server.StartAsync(cancellationToken);

        using var appClient = new TcpClient();
        await appClient.ConnectAsync(IPAddress.Loopback, server.Port, cancellationToken);
        await outbound.ConnectStarted.WaitAsync(cancellationToken);

        // Act
        Task stopTask = server.StopAsync(cancellationToken);
        await outbound.CancellationObserved.WaitAsync(cancellationToken);
        for (var attempt = 0; attempt < 100 && !stopTask.IsCompleted; attempt++)
        {
            await Task.Yield();
        }

        outbound.ReleaseCancellation();
        Exception? exception = await Record.ExceptionAsync(() => stopTask);

        // Assert
        Assert.Null(exception);
    }

    [Fact]
    public async Task BridgeAsync_WithKnownFakeIp_ConnectsOutboundAndRelaysBothDirections()
    {
        // Arrange
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");

        using var remoteListener = new TcpListener(IPAddress.Loopback, port: 0);
        remoteListener.Start();
        int remotePort = ((IPEndPoint)remoteListener.LocalEndpoint).Port;
        var outbound = new LoopbackOutbound(remotePort);
        var bridge = new FakeIpTcpBridge(pool, outbound);

        using var localListener = new TcpListener(IPAddress.Loopback, port: 0);
        localListener.Start();
        int localPort = ((IPEndPoint)localListener.LocalEndpoint).Port;

        using var appClient = new TcpClient();
        Task<TcpClient> acceptTask = localListener.AcceptTcpClientAsync(cancellationToken).AsTask();
        await appClient.ConnectAsync(IPAddress.Loopback, localPort, cancellationToken);
        using TcpClient acceptedClient = await acceptTask;
        await using NetworkStream appStream = appClient.GetStream();

        Task remoteTask = RunRemotePeerAsync(remoteListener, cancellationToken);

        // Act
        Task bridgeTask = bridge.BridgeAsync(acceptedClient.GetStream(), lease.FakeIp, 443, cancellationToken);
        await appStream.WriteAsync(new byte[] { 0x42 }, cancellationToken);
        byte[] response = new byte[1];
        int bytesRead = await appStream.ReadAsync(response, cancellationToken);
        appClient.Close();

        await bridgeTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
        await remoteTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);

        // Assert
        Assert.Equal("api.anthropic.com", outbound.Host);
        Assert.Equal(443, outbound.Port);
        Assert.Equal(1, bytesRead);
        Assert.Equal(0x24, response[0]);
    }

    [Fact]
    public async Task BridgeAsync_WithUnknownFakeIp_ThrowsInvalidOperationException()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var outbound = new FailingOutbound();
        var bridge = new FakeIpTcpBridge(pool, outbound);
        await using var stream = new MemoryStream();

        // Act
        Task act = bridge.BridgeAsync(stream, IPAddress.Parse("198.18.0.99"), 443, TestContext.Current.CancellationToken);

        // Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => act);
    }

    private static async Task RunRemotePeerAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = client.GetStream();
        byte[] request = new byte[1];
        int bytesRead = await stream.ReadAsync(request, cancellationToken);
        Assert.Equal(1, bytesRead);
        Assert.Equal(0x42, request[0]);
        await stream.WriteAsync(new byte[] { 0x24 }, cancellationToken);
    }

    private sealed class LoopbackOutbound(int remotePort) : IOutbound
    {
        public string? Host { get; private set; }

        public int? Port { get; private set; }

        public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            Host = host;
            Port = port;

            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, remotePort, cancellationToken);
                return client.GetStream();
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
    }

    private sealed class FailingOutbound : IOutbound
    {
        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Outbound should not be called for unknown fake IP.");
    }

    private sealed class BlockingOutbound : IOutbound
    {
        private readonly List<BlockingStream> _streams = [];
        private readonly TaskCompletionSource _connectStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ConnectStarted => _connectStarted.Task;

        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            var stream = new BlockingStream();
            _streams.Add(stream);
            _connectStarted.TrySetResult();
            return ValueTask.FromResult<Stream>(stream);
        }

        public void CompleteAllReads()
        {
            foreach (BlockingStream stream in _streams)
            {
                stream.CompleteRead();
            }
        }
    }

    private sealed class BlockingStream : Stream
    {
        private readonly TaskCompletionSource _readCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void CompleteRead()
            => _readCompletion.TrySetResult();

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _readCompletion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private sealed class ControlledCancellationOutbound : IOutbound
    {
        private readonly ControlledCancellationStream _stream = new();
        private readonly TaskCompletionSource _connectStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ConnectStarted => _connectStarted.Task;

        public Task CancellationObserved => _stream.CancellationObserved;

        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            _connectStarted.TrySetResult();
            return ValueTask.FromResult<Stream>(_stream);
        }

        public void ReleaseCancellation()
            => _stream.ReleaseCancellation();
    }

    private sealed class ControlledCancellationStream : Stream
    {
        private readonly TaskCompletionSource _cancellationObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CancellationObserved => _cancellationObserved.Task;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void ReleaseCancellation()
            => _releaseCancellation.TrySetResult();

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _cancellationObserved.TrySetResult();
                await _releaseCancellation.Task.ConfigureAwait(false);
                throw;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
