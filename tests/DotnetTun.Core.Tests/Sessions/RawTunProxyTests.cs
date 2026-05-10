using System.Net;
using DotnetTun.Abstractions;
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
        await proxy.PumpOnceAsync(123, TestContext.Current.CancellationToken);
        await proxy.PumpOnceAsync(123, TestContext.Current.CancellationToken);
        await proxy.PumpOnceAsync(123, TestContext.Current.CancellationToken);

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

    private sealed class QueueTunDevice(Queue<byte[]> packets) : ITunDevice
    {
        public List<byte[]> WrittenPackets { get; } = [];

        public Task<TunDeviceOpenResult> OpenTunAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(TunDeviceOpenResult.Opened(123, "tun-test"));

        public ValueTask<TunPacketIoResult> ReadPacketAsync(int fileDescriptor, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            byte[] packet = packets.Dequeue();
            packet.CopyTo(buffer);
            return ValueTask.FromResult(TunPacketIoResult.Transferred(packet.Length));
        }

        public ValueTask<TunPacketIoResult> WritePacketAsync(int fileDescriptor, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            WrittenPackets.Add(packet.ToArray());
            return ValueTask.FromResult(TunPacketIoResult.Transferred(packet.Length));
        }

        public ValueTask<TunDeviceCloseResult> CloseTunAsync(int fileDescriptor, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TunDeviceCloseResult.Closed());
    }

    private sealed class RecordingOutbound : IOutbound
    {
        public RecordingStream Stream { get; } = new();

        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<Stream>(Stream);
    }

    private sealed class RecordingStream : MemoryStream
    {
        public byte[] WrittenBytes => ToArray();
    }

    private sealed class BlockingQueueTunDevice(Queue<byte[]> packets) : ITunDevice
    {
        private readonly TaskCompletionSource<byte[]> _thirdWrittenPacket = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<byte[]> WrittenPackets { get; } = [];

        public Task<TunDeviceOpenResult> OpenTunAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(TunDeviceOpenResult.Opened(123, "tun-test"));

        public ValueTask<TunPacketIoResult> ReadPacketAsync(int fileDescriptor, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (packets.TryDequeue(out byte[]? packet))
            {
                packet.CopyTo(buffer);
                return ValueTask.FromResult(TunPacketIoResult.Transferred(packet.Length));
            }

            return new ValueTask<TunPacketIoResult>(WaitForCancellationAsync(cancellationToken));
        }

        public ValueTask<TunPacketIoResult> WritePacketAsync(int fileDescriptor, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            byte[] writtenPacket = packet.ToArray();
            WrittenPackets.Add(writtenPacket);
            if (WrittenPackets.Count == 3)
            {
                _thirdWrittenPacket.TrySetResult(writtenPacket);
            }

            return ValueTask.FromResult(TunPacketIoResult.Transferred(packet.Length));
        }

        public ValueTask<TunDeviceCloseResult> CloseTunAsync(int fileDescriptor, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TunDeviceCloseResult.Closed());

        public Task<byte[]> ThirdWrittenPacketAsync(CancellationToken cancellationToken)
            => _thirdWrittenPacket.Task.WaitAsync(cancellationToken);

        private static async Task<TunPacketIoResult> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return TunPacketIoResult.Transferred(0);
        }
    }

    private sealed class DelayedResponseOutbound : IOutbound
    {
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
}
