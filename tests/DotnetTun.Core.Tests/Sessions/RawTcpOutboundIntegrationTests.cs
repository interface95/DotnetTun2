using System.Net;
using DotnetTun.Abstractions;
using DotnetTun.Core.Dns;
using DotnetTun.Core.Packets;
using DotnetTun.Core.Tests.Packets;
using DotnetTun.Core.Sessions;
using Xunit;

namespace DotnetTun.Core.Tests.Sessions;

public sealed class RawTcpOutboundIntegrationTests
{
    [Fact]
    public async Task HandleAsync_WithEstablishedPayload_WritesPayloadToResolvedOutbound()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");
        var outbound = new RecordingOutbound();
        await using var sink = new OutboundTcpPayloadSink(pool, outbound);
        var handler = new RawTcpSessionHandler(new TcpSessionTable(), serverInitialSequence: 9_000, sink);

        await HandlePacketAsync(handler, PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: lease.FakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_000,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn));

        await HandlePacketAsync(handler, PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: lease.FakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Ack));

        byte[] payload = [0x42, 0x43];

        // Act
        IReadOnlyList<ReadOnlyMemory<byte>> responses = await HandlePacketAsync(handler, PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: lease.FakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Psh | TcpFlags.Ack,
            payload: payload));

        // Assert
        Assert.Equal("api.anthropic.com", outbound.Host);
        Assert.Equal(443, outbound.Port);
        Assert.Equal(payload, outbound.Stream.WrittenBytes.ToArray());
        Assert.Single(responses);
    }

    [Fact]
    public async Task HandleAsync_WithOutboundResponse_ReturnsServerPayloadPacket()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");
        byte[] outboundResponse = [0x24];
        var outbound = new RecordingOutbound(outboundResponse);
        await using var sink = new OutboundTcpPayloadSink(pool, outbound, responseReadTimeout: TimeSpan.FromMilliseconds(100));
        var sessions = new TcpSessionTable();
        var handler = new RawTcpSessionHandler(sessions, serverInitialSequence: 9_000, sink);

        await HandlePacketAsync(handler, PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: lease.FakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_000,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn));

        await HandlePacketAsync(handler, PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: lease.FakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Ack));

        byte[] payload = [0x42, 0x43];

        // Act
        IReadOnlyList<ReadOnlyMemory<byte>> responses = await HandlePacketAsync(handler, PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: lease.FakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Psh | TcpFlags.Ack,
            payload: payload));

        // Assert
        Assert.Equal(2, responses.Count);
        Assert.True(Ipv4Packet.TryParse(responses[1], out Ipv4Packet responseIp));
        Assert.True(TcpSegment.TryParse(responseIp, out TcpSegment responseTcp));
        Assert.Equal("198.18.0.1", responseIp.SourceAddress.ToString());
        Assert.Equal("10.0.0.2", responseIp.DestinationAddress.ToString());
        Assert.Equal(TcpFlags.Psh | TcpFlags.Ack, responseTcp.Flags);
        Assert.Equal(443, responseTcp.SourcePort);
        Assert.Equal(54321, responseTcp.DestinationPort);
        Assert.Equal(9_001u, responseTcp.SequenceNumber);
        Assert.Equal(1_003u, responseTcp.AcknowledgmentNumber);
        Assert.Equal(outboundResponse, responseTcp.Payload.ToArray());
        Assert.True(TcpChecksum.IsValid(responseIp, responseTcp));

        var key = new TcpFlowKey(IPAddress.Parse("10.0.0.2"), 54321, lease.FakeIp, 443);
        Assert.True(sessions.TryGet(key, out TcpSession? session));
        Assert.NotNull(session);
        Assert.Equal(9_002u, session.Value.NextServerSequence);
    }

    private static async Task<IReadOnlyList<ReadOnlyMemory<byte>>> HandlePacketAsync(RawTcpSessionHandler handler, byte[] packet)
    {
        Assert.True(Ipv4Packet.TryParse(packet, out Ipv4Packet ipPacket));
        Assert.True(TcpSegment.TryParse(ipPacket, out TcpSegment tcpSegment));
        return await handler.HandleAsync(ipPacket, tcpSegment, TestContext.Current.CancellationToken);
    }

    private sealed class RecordingOutbound(byte[]? response = null) : IOutbound
    {
        public string Name => "test";

        public string? Host { get; private set; }

        public int? Port { get; private set; }

        public RecordingStream Stream { get; } = new(response ?? []);

        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
        {
            Host = host;
            Port = port;
            return ValueTask.FromResult<Stream>(Stream);
        }
    }

    private sealed class RecordingStream(byte[] response) : Stream
    {
        private readonly MemoryStream _writes = new();
        private readonly MemoryStream _reads = new(response);

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

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
            => _reads.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value)
            => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => _writes.Write(buffer, offset, count);
    }
}
