using System.Net;
using DotnetTun.Abstractions;
using DotnetTun.Abstractions.Dns;
using DotnetTun.Abstractions.Routing;
using DotnetTun.Core.Dns;
using DotnetTun.Core.Sessions;
using Xunit;

namespace DotnetTun.Core.Tests.Sessions;

public sealed class OutboundTcpPayloadSinkTests
{
    [Fact]
    public async Task WriteAsync_WithKnownFakeIp_ConnectsOutboundAndWritesPayload()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");
        var outbound = new RecordingOutbound();
        var sink = new OutboundTcpPayloadSink(pool, outbound);
        var key = new TcpFlowKey(IPAddress.Parse("10.0.0.2"), 54321, lease.FakeIp, 443);
        var session = new TcpSession(key, 1_000, 9_000, 1_001, 9_001, TcpSessionState.Established);
        byte[] payload = [0x42, 0x43];

        // Act
        await sink.WriteAsync(session, payload, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("api.anthropic.com", outbound.Host);
        Assert.Equal(443, outbound.Port);
        Assert.Equal(payload, outbound.Stream.WrittenBytes.ToArray());
        Assert.True(outbound.Stream.Flushed);
    }

    [Fact]
    public async Task WriteAsync_WithSameSession_ReusesOutboundStream()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");
        var outbound = new RecordingOutbound();
        var sink = new OutboundTcpPayloadSink(pool, outbound);
        var key = new TcpFlowKey(IPAddress.Parse("10.0.0.2"), 54321, lease.FakeIp, 443);
        var session = new TcpSession(key, 1_000, 9_000, 1_001, 9_001, TcpSessionState.Established);

        // Act
        await sink.WriteAsync(session, new byte[] { 0x42 }, TestContext.Current.CancellationToken);
        await sink.WriteAsync(session, new byte[] { 0x43 }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, outbound.ConnectCount);
        Assert.Equal(new byte[] { 0x42, 0x43 }, outbound.Stream.WrittenBytes.ToArray());
    }

    [Fact]
    public async Task DisposeAsync_WithCachedStream_DisposesOutboundStream()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");
        var outbound = new RecordingOutbound();
        var sink = new OutboundTcpPayloadSink(pool, outbound);
        var key = new TcpFlowKey(IPAddress.Parse("10.0.0.2"), 54321, lease.FakeIp, 443);
        var session = new TcpSession(key, 1_000, 9_000, 1_001, 9_001, TcpSessionState.Established);
        await sink.WriteAsync(session, new byte[] { 0x42 }, TestContext.Current.CancellationToken);

        // Act
        await sink.DisposeAsync();

        // Assert
        Assert.True(outbound.Stream.Disposed);
    }

    [Fact]
    public async Task CloseAsync_WithCachedSession_DisposesStreamAndAllowsReconnect()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");
        var outbound = new RecordingOutbound();
        var sink = new OutboundTcpPayloadSink(pool, outbound);
        var key = new TcpFlowKey(IPAddress.Parse("10.0.0.2"), 54321, lease.FakeIp, 443);
        var session = new TcpSession(key, 1_000, 9_000, 1_001, 9_001, TcpSessionState.Established);
        await sink.WriteAsync(session, new byte[] { 0x42 }, TestContext.Current.CancellationToken);

        // Act
        await sink.CloseAsync(session, TestContext.Current.CancellationToken);
        await sink.WriteAsync(session, new byte[] { 0x43 }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, outbound.ConnectCount);
        Assert.True(outbound.Streams[0].Disposed);
        Assert.Equal(new byte[] { 0x43 }, outbound.Streams[1].WrittenBytes.ToArray());
    }

    [Fact]
    public async Task WriteAsync_WithoutResponseReadTimeout_ReturnsNoResponsePayloads()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");
        var outbound = new RecordingOutbound(response: [0x24]);
        var sink = new OutboundTcpPayloadSink(pool, outbound);
        var key = new TcpFlowKey(IPAddress.Parse("10.0.0.2"), 54321, lease.FakeIp, 443);
        var session = new TcpSession(key, 1_000, 9_000, 1_001, 9_001, TcpSessionState.Established);

        // Act
        IReadOnlyList<ReadOnlyMemory<byte>> responses = await sink.WriteAsync(session, new byte[] { 0x42 }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(responses);
    }

    [Fact]
    public async Task WriteAsync_WithRemotePayloadHandler_ReturnsAfterFlushAndPublishesDelayedResponse()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");
        var outbound = new DelayedResponseOutbound();
        var responseReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var sink = new OutboundTcpPayloadSink(
            pool,
            outbound,
            remotePayloadHandler: (_, payload, _) =>
            {
                responseReceived.TrySetResult(payload.ToArray());
                return ValueTask.CompletedTask;
            });
        var key = new TcpFlowKey(IPAddress.Parse("10.0.0.2"), 54321, lease.FakeIp, 443);
        var session = new TcpSession(key, 1_000, 9_000, 1_001, 9_001, TcpSessionState.Established);
        byte[] requestPayload = [0x42];
        byte[] responsePayload = [0x24];

        // Act
        IReadOnlyList<ReadOnlyMemory<byte>> inlineResponses = await sink.WriteAsync(session, requestPayload, TestContext.Current.CancellationToken);
        outbound.Stream.PublishResponse(responsePayload);
        byte[] publishedResponse = await responseReceived.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(inlineResponses);
        Assert.Equal(requestPayload, outbound.Stream.WrittenBytes.ToArray());
        Assert.True(outbound.Stream.Flushed);
        Assert.Equal(responsePayload, publishedResponse);
    }

    [Fact]
    public async Task WriteAsync_WhenActiveSessionLimitExceeded_RejectsNewSession()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var firstLease = pool.Allocate("api.anthropic.com");
        var secondLease = pool.Allocate("api.openai.com");
        var outbound = new RecordingOutbound();
        var sink = new OutboundTcpPayloadSink(pool, outbound, maxActiveSessions: 1);
        var firstSession = new TcpSession(
            new TcpFlowKey(IPAddress.Parse("10.0.0.2"), 54321, firstLease.FakeIp, 443),
            1_000,
            9_000,
            1_001,
            9_001,
            TcpSessionState.Established);
        var secondSession = new TcpSession(
            new TcpFlowKey(IPAddress.Parse("10.0.0.3"), 54322, secondLease.FakeIp, 443),
            2_000,
            9_000,
            2_001,
            9_001,
            TcpSessionState.Established);
        await sink.WriteAsync(firstSession, new byte[] { 0x42 }, TestContext.Current.CancellationToken);

        // Act
        async Task WriteSecondSessionAsync()
            => await sink.WriteAsync(secondSession, new byte[] { 0x43 }, TestContext.Current.CancellationToken);

        // Assert
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(WriteSecondSessionAsync);
        Assert.Contains("active outbound TCP session limit", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, outbound.ConnectCount);
    }

    [Fact]
    public async Task WriteAsync_WithRoutedFakeIp_UsesRouterSelectedOutbound()
    {
        // Arrange
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var fakeIp = store.Allocate("api.anthropic.com");
        var router = new RecordingRouter(RouteDecision.Through("premium"));
        var defaultOutbound = new RecordingOutbound { Name = "default" };
        var premiumOutbound = new RecordingOutbound { Name = "premium" };
        var sink = new OutboundTcpPayloadSink(
            store,
            router,
            new Dictionary<string, IOutbound>(StringComparer.OrdinalIgnoreCase)
            {
                [defaultOutbound.Name] = defaultOutbound,
                [premiumOutbound.Name] = premiumOutbound,
            });
        var key = new TcpFlowKey(IPAddress.Parse("10.0.0.2"), 54321, fakeIp, 443);
        var session = new TcpSession(key, 1_000, 9_000, 1_001, 9_001, TcpSessionState.Established);
        byte[] payload = [0x42, 0x43];

        // Act
        await sink.WriteAsync(session, payload, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new ConnectionContext("api.anthropic.com", 443), router.Context);
        Assert.Equal(0, defaultOutbound.ConnectCount);
        Assert.Equal("api.anthropic.com", premiumOutbound.Host);
        Assert.Equal(443, premiumOutbound.Port);
        Assert.Equal(payload, premiumOutbound.Stream.WrittenBytes.ToArray());
    }

    [Fact]
    public async Task WriteAsync_WithRoutedFakeIpAndSameSession_ReusesSelectedOutboundStream()
    {
        // Arrange
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var fakeIp = store.Allocate("api.anthropic.com");
        var router = new RecordingRouter(RouteDecision.Through("premium"));
        var premiumOutbound = new RecordingOutbound { Name = "premium" };
        var sink = new OutboundTcpPayloadSink(
            store,
            router,
            new Dictionary<string, IOutbound>(StringComparer.OrdinalIgnoreCase)
            {
                [premiumOutbound.Name] = premiumOutbound,
            });
        var key = new TcpFlowKey(IPAddress.Parse("10.0.0.2"), 54321, fakeIp, 443);
        var session = new TcpSession(key, 1_000, 9_000, 1_001, 9_001, TcpSessionState.Established);

        // Act
        await sink.WriteAsync(session, new byte[] { 0x42 }, TestContext.Current.CancellationToken);
        await sink.WriteAsync(session, new byte[] { 0x43 }, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, router.CallCount);
        Assert.Equal(1, premiumOutbound.ConnectCount);
        Assert.Equal(new byte[] { 0x42, 0x43 }, premiumOutbound.Stream.WrittenBytes.ToArray());
    }

    [Fact]
    public async Task WriteAsync_WithRoutedUnknownFakeIp_RejectsBeforeRouting()
    {
        // Arrange
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var router = new RecordingRouter(RouteDecision.Through("premium"));
        var premiumOutbound = new RecordingOutbound { Name = "premium" };
        var sink = new OutboundTcpPayloadSink(
            store,
            router,
            new Dictionary<string, IOutbound>(StringComparer.OrdinalIgnoreCase)
            {
                [premiumOutbound.Name] = premiumOutbound,
            });
        var key = new TcpFlowKey(IPAddress.Parse("10.0.0.2"), 54321, IPAddress.Parse("198.18.0.99"), 443);
        var session = new TcpSession(key, 1_000, 9_000, 1_001, 9_001, TcpSessionState.Established);

        // Act
        async Task WriteAsync()
            => await sink.WriteAsync(session, new byte[] { 0x42 }, TestContext.Current.CancellationToken);

        // Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(WriteAsync);
        Assert.Contains("No domain lease exists", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, router.CallCount);
        Assert.Equal(0, premiumOutbound.ConnectCount);
    }

    [Fact]
    public async Task WriteAsync_WithRoutedDirectDecision_RejectsWithoutConnectingOutbound()
    {
        // Arrange
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var fakeIp = store.Allocate("api.anthropic.com");
        var router = new RecordingRouter(RouteDecision.Direct());
        var premiumOutbound = new RecordingOutbound { Name = "premium" };
        var sink = new OutboundTcpPayloadSink(
            store,
            router,
            new Dictionary<string, IOutbound>(StringComparer.OrdinalIgnoreCase)
            {
                [premiumOutbound.Name] = premiumOutbound,
            });
        var key = new TcpFlowKey(IPAddress.Parse("10.0.0.2"), 54321, fakeIp, 443);
        var session = new TcpSession(key, 1_000, 9_000, 1_001, 9_001, TcpSessionState.Established);

        // Act
        async Task WriteAsync()
            => await sink.WriteAsync(session, new byte[] { 0x42 }, TestContext.Current.CancellationToken);

        // Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(WriteAsync);
        Assert.Contains("Direct TCP routing decisions are not supported", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, router.CallCount);
        Assert.Equal(0, premiumOutbound.ConnectCount);
    }

    [Fact]
    public async Task WriteAsync_WithRoutedMissingOutbound_RejectsWithoutConnectingOutbound()
    {
        // Arrange
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var fakeIp = store.Allocate("api.anthropic.com");
        var router = new RecordingRouter(RouteDecision.Through("missing"));
        var premiumOutbound = new RecordingOutbound { Name = "premium" };
        var sink = new OutboundTcpPayloadSink(
            store,
            router,
            new Dictionary<string, IOutbound>(StringComparer.OrdinalIgnoreCase)
            {
                [premiumOutbound.Name] = premiumOutbound,
            });
        var key = new TcpFlowKey(IPAddress.Parse("10.0.0.2"), 54321, fakeIp, 443);
        var session = new TcpSession(key, 1_000, 9_000, 1_001, 9_001, TcpSessionState.Established);

        // Act
        async Task WriteAsync()
            => await sink.WriteAsync(session, new byte[] { 0x42 }, TestContext.Current.CancellationToken);

        // Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(WriteAsync);
        Assert.Contains("No outbound named 'missing' is registered", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1, router.CallCount);
        Assert.Equal(0, premiumOutbound.ConnectCount);
    }

    private sealed class RecordingOutbound(byte[]? response = null) : IOutbound
    {
        public string Name { get; init; } = "test";

        public string? Host { get; private set; }

        public int? Port { get; private set; }

        public List<RecordingStream> Streams { get; } = [];

        public RecordingStream Stream => Streams.Last();

        public int ConnectCount { get; private set; }

        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            Host = host;
            Port = port;
            var stream = new RecordingStream(response ?? []);
            Streams.Add(stream);
            return ValueTask.FromResult<Stream>(stream);
        }
    }

    private sealed class RecordingStream(byte[] response) : MemoryStream(response)
    {
        private readonly MemoryStream _writes = new();

        public bool Flushed { get; private set; }

        public bool Disposed { get; private set; }

        public byte[] WrittenBytes => _writes.ToArray();

        public override void Write(byte[] buffer, int offset, int count)
            => _writes.Write(buffer, offset, count);

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushed = true;
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class DelayedResponseOutbound : IOutbound
    {
        public string Name => "test";

        public DelayedResponseStream Stream { get; } = new();

        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<Stream>(Stream);
    }

    private sealed class RecordingRouter(RouteDecision decision) : IRouter
    {
        public ConnectionContext? Context { get; private set; }

        public int CallCount { get; private set; }

        public ValueTask<RouteDecision> RouteAsync(ConnectionContext context, CancellationToken cancellationToken = default)
        {
            CallCount++;
            Context = context;
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class DelayedResponseStream : Stream
    {
        private readonly MemoryStream _writes = new();
        private readonly TaskCompletionSource<byte[]> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _responseConsumed;

        public bool Flushed { get; private set; }

        public byte[] WrittenBytes => _writes.ToArray();

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public void PublishResponse(byte[] response)
            => _response.TrySetResult(response);

        public override void Flush()
            => Flushed = true;

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            Flushed = true;
            return Task.CompletedTask;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_responseConsumed)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }

            byte[] response = await _response.Task.WaitAsync(cancellationToken);
            _responseConsumed = true;
            response.CopyTo(buffer);
            return response.Length;
        }

        public override int Read(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => _writes.Write(buffer, offset, count);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _writes.Write(buffer.Span);
            return ValueTask.CompletedTask;
        }
    }
}
