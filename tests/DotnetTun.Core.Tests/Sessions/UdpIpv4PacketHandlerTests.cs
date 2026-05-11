using DotnetTun.Core.Packets;
using DotnetTun.Core.Sessions;
using DotnetTun.Core.Tests.Packets;
using Xunit;

namespace DotnetTun.Core.Tests.Sessions;

public sealed class UdpIpv4PacketHandlerTests
{
    [Fact]
    public async Task HandleAsync_WithValidUdpPacket_DelegatesDatagram()
    {
        var packet = PacketFixtures.CreateUdpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            sourcePort: 53000,
            destinationPort: 53,
            payload: [0x12, 0x34]);
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));
        byte[] response = [0x45, 0x00];
        var segmentHandler = new RecordingUdpDatagramHandler(response);
        var handler = new UdpIpv4PacketHandler(segmentHandler);

        var responses = await handler.HandleAsync(ipv4Packet, TestContext.Current.CancellationToken);

        Assert.Equal("10.0.0.1", segmentHandler.Packet.SourceAddress.ToString());
        Assert.Equal(53000, segmentHandler.Datagram.SourcePort);
        Assert.Equal(53, segmentHandler.Datagram.DestinationPort);
        Assert.Collection(responses, item => Assert.Equal(response, item.ToArray()));
    }

    [Fact]
    public async Task HandleAsync_WithInvalidUdpChecksum_DropsPacket()
    {
        var packet = PacketFixtures.CreateUdpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            sourcePort: 53000,
            destinationPort: 53,
            payload: [0x12, 0x34]);
        packet[^1] ^= 0xFF;
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));
        var segmentHandler = new RecordingUdpDatagramHandler([0x45, 0x00]);
        var handler = new UdpIpv4PacketHandler(segmentHandler);

        var responses = await handler.HandleAsync(ipv4Packet, TestContext.Current.CancellationToken);

        Assert.Empty(responses);
        Assert.Equal(default, segmentHandler.Datagram);
    }

    private sealed class RecordingUdpDatagramHandler(byte[] response) : IUdpDatagramHandler
    {
        public Ipv4Packet Packet { get; private set; }

        public UdpDatagram Datagram { get; private set; }

        public ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(Ipv4Packet packet, UdpDatagram datagram, CancellationToken cancellationToken = default)
        {
            Packet = packet;
            Datagram = datagram;
            IReadOnlyList<ReadOnlyMemory<byte>> responses = [response];
            return ValueTask.FromResult(responses);
        }
    }
}
