using DotnetTun.Core.Packets;
using DotnetTun.Core.Tests.Packets;
using DotnetTun.Core.Sessions;
using Xunit;

namespace DotnetTun.Core.Tests.Sessions;

public sealed class Ipv4TunPacketHandlerTests
{
    [Fact]
    public async Task HandleAsync_WithValidIpv4Packet_DelegatesParsedPacket()
    {
        // Arrange
        var packet = PacketFixtures.CreateTcpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            54321,
            443,
            1,
            0,
            TcpFlags.Syn);
        byte[] response = [0x45, 0x00];
        var handler = new RecordingIpv4PacketHandler(response);
        var adapter = new Ipv4TunPacketHandler(handler);

        // Act
        IReadOnlyList<ReadOnlyMemory<byte>> responses = await adapter.HandleAsync(packet, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("10.0.0.1", handler.Packet.SourceAddress.ToString());
        Assert.Equal("198.18.0.1", handler.Packet.DestinationAddress.ToString());
        Assert.Collection(responses, item => Assert.Equal(response, item.ToArray()));
    }

    private sealed class RecordingIpv4PacketHandler(byte[] response) : IIpv4PacketHandler
    {
        public Ipv4Packet Packet { get; private set; }

        public ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(Ipv4Packet packet, CancellationToken cancellationToken = default)
        {
            Packet = packet;
            IReadOnlyList<ReadOnlyMemory<byte>> responses = [response];
            return ValueTask.FromResult(responses);
        }
    }
}
