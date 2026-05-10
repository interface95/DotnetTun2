using System.Net;
using DotnetTun.Abstractions;
using DotnetTun.Core.Dns;
using DotnetTun.Core.Packets;
using DotnetTun.Core.Tests.Packets;
using DotnetTun.Core.Sessions;
using Xunit;

namespace DotnetTun.Core.Tests.Sessions;

public sealed class RawTcpTunPipelineTests
{
    [Fact]
    public async Task CreateHandler_WithEstablishedPayload_WritesPayloadToOutboundAndReturnsAck()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");
        var outbound = new RecordingOutbound();
        await using RawTcpTunPipeline pipeline = RawTcpTunPipeline.Create(pool, outbound, serverInitialSequence: 9_000);
        ITunPacketHandler handler = pipeline.PacketHandler;

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

        byte[] payload = [0x42];

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
        Assert.Equal(payload, outbound.Stream.WrittenBytes.ToArray());
        Assert.Single(responses);
    }

    private static async Task<IReadOnlyList<ReadOnlyMemory<byte>>> HandlePacketAsync(ITunPacketHandler handler, byte[] packet)
        => await handler.HandleAsync(packet, TestContext.Current.CancellationToken);

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
}
