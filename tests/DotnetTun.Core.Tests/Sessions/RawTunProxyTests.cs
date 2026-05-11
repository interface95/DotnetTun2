using System.Net;
using DotnetTun.Abstractions;
using DotnetTun.Abstractions.Dns;
using DotnetTun.Abstractions.Routing;
using DotnetTun.Core.Dns;
using DotnetTun.Core.Packets;
using DotnetTun.Core.Tests.Packets;
using DotnetTun.Core.Sessions;
using Xunit;

namespace DotnetTun.Core.Tests.Sessions;

public sealed class RawTunProxyTests
{
    [Fact]
    public async Task PumpOnceAsync_WithHandshakeAndPayload_WritesOutboundAndTunResponses()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");
        var outbound = new RecordingOutbound();
        byte[] syn = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: lease.FakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_000,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn);
        byte[] ack = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: lease.FakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Ack);
        byte[] payload = [0x42];
        byte[] psh = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: lease.FakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Psh | TcpFlags.Ack,
            payload: payload);
        var device = new QueueTunDevice(new Queue<byte[]>([syn, ack, psh]));
        await using RawTunProxy proxy = RawTunProxy.Create(device, pool, outbound, serverInitialSequence: 9_000, mtu: 1500);

        // Act
        await device.OpenAsync(TestContext.Current.CancellationToken);
        await proxy.PumpOnceAsync(TestContext.Current.CancellationToken);
        await proxy.PumpOnceAsync(TestContext.Current.CancellationToken);
        await proxy.PumpOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(payload, outbound.Stream.WrittenBytes.ToArray());
        Assert.Equal(2, device.WrittenPackets.Count);
        Assert.True(Ipv4Packet.TryParse(device.WrittenPackets[0], out Ipv4Packet synAckIp));
        Assert.True(TcpSegment.TryParse(synAckIp, out TcpSegment synAckTcp));
        Assert.Equal(TcpFlags.Syn | TcpFlags.Ack, synAckTcp.Flags);
        Assert.True(Ipv4Packet.TryParse(device.WrittenPackets[1], out Ipv4Packet payloadAckIp));
        Assert.True(TcpSegment.TryParse(payloadAckIp, out TcpSegment payloadAckTcp));
        Assert.Equal(TcpFlags.Ack, payloadAckTcp.Flags);
    }

    [Fact]
    public async Task RunAsync_WithDelayedOutboundResponse_WritesServerPayloadToTun()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");
        var outbound = new DelayedResponseOutbound();
        byte[] syn = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: lease.FakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_000,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn);
        byte[] ack = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: lease.FakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Ack);
        byte[] requestPayload = [0x42];
        byte[] psh = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: lease.FakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Psh | TcpFlags.Ack,
            payload: requestPayload);
        byte[] responsePayload = [0x24];
        var device = new BlockingQueueTunDevice(new Queue<byte[]>([syn, ack, psh]));
        await using RawTunProxy proxy = RawTunProxy.Create(device, pool, outbound, serverInitialSequence: 9_000, mtu: 1500);
        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task runTask = proxy.RunAsync(stopSource.Token);
        using var responseWaitSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        responseWaitSource.CancelAfter(TimeSpan.FromSeconds(1));
        await outbound.Stream.RequestWritten.Task.WaitAsync(responseWaitSource.Token);

        // Act
        byte[] serverPayloadPacket;
        try
        {
            outbound.Stream.PublishResponse(responsePayload);
            serverPayloadPacket = await device.ThirdWrittenPacketAsync(responseWaitSource.Token);
        }
        finally
        {
            await stopSource.CancelAsync();
            await runTask;
        }

        // Assert
        Assert.True(Ipv4Packet.TryParse(serverPayloadPacket, out Ipv4Packet responseIp));
        Assert.True(TcpSegment.TryParse(responseIp, out TcpSegment responseTcp));
        Assert.Equal(TcpFlags.Psh | TcpFlags.Ack, responseTcp.Flags);
        Assert.Equal(443, responseTcp.SourcePort);
        Assert.Equal(54321, responseTcp.DestinationPort);
        Assert.Equal(9_001u, responseTcp.SequenceNumber);
        Assert.Equal(1_002u, responseTcp.AcknowledgmentNumber);
        Assert.Equal(responsePayload, responseTcp.Payload.ToArray());
        Assert.True(TcpChecksum.IsValid(responseIp, responseTcp));
    }

    [Fact]
    public async Task PumpOnceAsync_WithDnsHijacker_WritesUdpDnsResponseToTun()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var outbound = new RecordingOutbound();
        var query = PacketFixtures.CreateUdpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 53000,
            destinationPort: 53,
            payload: [0x12, 0x34]);
        var device = new QueueTunDevice(new Queue<byte[]>([query]));
        var hijacker = new StubDnsHijacker(DnsHandlingResult.Intercepted([0xAB, 0xCD]));
        await using RawTunProxy proxy = RawTunProxy.Create(device, pool, outbound, serverInitialSequence: 9_000, mtu: 1500, dnsHijacker: hijacker);

        // Act
        await device.OpenAsync(TestContext.Current.CancellationToken);
        await proxy.PumpOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        var responsePacket = Assert.Single(device.WrittenPackets);
        Assert.True(Ipv4Packet.TryParse(responsePacket, out var responseIp));
        Assert.True(UdpDatagram.TryParse(responseIp, out var responseUdp));
        Assert.Equal([0x12, 0x34], hijacker.Query.ToArray());
        Assert.Equal([0xAB, 0xCD], responseUdp.Payload.ToArray());
        Assert.Equal(53, responseUdp.SourcePort);
        Assert.Equal(53000, responseUdp.DestinationPort);
    }

    [Fact]
    public async Task PumpOnceAsync_WithRoutedFakeIp_WritesPayloadToSelectedOutbound()
    {
        // Arrange
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var fakeIp = store.Allocate("api.anthropic.com");
        var router = new RecordingRouter(RouteDecision.Through("premium"));
        var defaultOutbound = new RecordingOutbound { Name = "default" };
        var premiumOutbound = new RecordingOutbound { Name = "premium" };
        byte[] syn = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: fakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_000,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn);
        byte[] ack = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: fakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Ack);
        byte[] payload = [0x42];
        byte[] psh = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: fakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Psh | TcpFlags.Ack,
            payload: payload);
        var device = new QueueTunDevice(new Queue<byte[]>([syn, ack, psh]));
        await using RawTunProxy proxy = RawTunProxy.Create(
            tunDevice: device,
            fakeIpStore: store,
            router: router,
            outbounds: new Dictionary<string, IOutbound>(StringComparer.OrdinalIgnoreCase)
            {
                [defaultOutbound.Name] = defaultOutbound,
                [premiumOutbound.Name] = premiumOutbound,
            },
            serverInitialSequence: 9_000,
            mtu: 1500);

        // Act
        await device.OpenAsync(TestContext.Current.CancellationToken);
        await proxy.PumpOnceAsync(TestContext.Current.CancellationToken);
        await proxy.PumpOnceAsync(TestContext.Current.CancellationToken);
        await proxy.PumpOnceAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new ConnectionContext("api.anthropic.com", 443), router.Context);
        Assert.Empty(defaultOutbound.Stream.WrittenBytes);
        Assert.Equal(payload, premiumOutbound.Stream.WrittenBytes.ToArray());
        Assert.Equal(2, device.WrittenPackets.Count);
    }

    private sealed class QueueTunDevice(Queue<byte[]> packets) : ITunDevice
    {
        public List<byte[]> WrittenPackets { get; } = [];

        public bool IsOpen { get; private set; }

        public string? InterfaceName { get; private set; }

        public ValueTask OpenAsync(CancellationToken cancellationToken = default)
        {
            IsOpen = true;
            InterfaceName = "tun-test";
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureOpen();
            byte[] packet = packets.Dequeue();
            packet.CopyTo(buffer);
            return ValueTask.FromResult(packet.Length);
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            EnsureOpen();
            WrittenPackets.Add(packet.ToArray());
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            IsOpen = false;
            InterfaceName = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => CloseAsync();

        private void EnsureOpen()
        {
            if (!IsOpen)
            {
                throw new InvalidOperationException("TUN device is not open.");
            }
        }
    }

    private sealed class RecordingOutbound : IOutbound
    {
        public string Name { get; init; } = "test";

        public RecordingStream Stream { get; } = new();

        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<Stream>(Stream);
    }

    private sealed class RecordingRouter(RouteDecision decision) : IRouter
    {
        public ConnectionContext? Context { get; private set; }

        public ValueTask<RouteDecision> RouteAsync(ConnectionContext context, CancellationToken cancellationToken = default)
        {
            Context = context;
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class RecordingStream : MemoryStream
    {
        public byte[] WrittenBytes => ToArray();
    }

    private sealed class BlockingQueueTunDevice(Queue<byte[]> packets) : ITunDevice
    {
        private readonly TaskCompletionSource<byte[]> _thirdWrittenPacket = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<byte[]> WrittenPackets { get; } = [];

        public bool IsOpen { get; private set; }

        public string? InterfaceName { get; private set; }

        public ValueTask OpenAsync(CancellationToken cancellationToken = default)
        {
            IsOpen = true;
            InterfaceName = "tun-test";
            return ValueTask.CompletedTask;
        }

        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            EnsureOpen();
            if (packets.TryDequeue(out byte[]? packet))
            {
                packet.CopyTo(buffer);
                return ValueTask.FromResult(packet.Length);
            }

            return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            EnsureOpen();
            byte[] writtenPacket = packet.ToArray();
            WrittenPackets.Add(writtenPacket);
            if (WrittenPackets.Count == 3)
            {
                _thirdWrittenPacket.TrySetResult(writtenPacket);
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask CloseAsync(CancellationToken cancellationToken = default)
        {
            IsOpen = false;
            InterfaceName = null;
            return ValueTask.CompletedTask;
        }

        public Task<byte[]> ThirdWrittenPacketAsync(CancellationToken cancellationToken)
            => _thirdWrittenPacket.Task.WaitAsync(cancellationToken);

        public ValueTask DisposeAsync() => CloseAsync();

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        private void EnsureOpen()
        {
            if (!IsOpen)
            {
                throw new InvalidOperationException("TUN device is not open.");
            }
        }
    }

    private sealed class DelayedResponseOutbound : IOutbound
    {
        public string Name => "test";

        public DelayedResponseStream Stream { get; } = new();

        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<Stream>(Stream);
    }

    private sealed class DelayedResponseStream : Stream
    {
        private readonly MemoryStream _writes = new();
        private readonly TaskCompletionSource<byte[]> _response = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _responseConsumed;

        public TaskCompletionSource RequestWritten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

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
        {
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_responseConsumed)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return 0;
            }

            byte[] response = await _response.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
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
            RequestWritten.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubDnsHijacker(DnsHandlingResult result) : IDnsHijacker
    {
        public ReadOnlyMemory<byte> Query { get; private set; }

        public ValueTask<DnsHandlingResult> HandleAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken = default)
        {
            Query = query;
            return ValueTask.FromResult(result);
        }
    }
}
