using DotnetTun.Core.Packets;
using DotnetTun.Core.Tests.Packets;
using Xunit;

namespace DotnetTun.Core.Tests.Packets;

public sealed class UdpDatagramTests
{
    [Fact]
    public void TryParse_WithValidUdpPacket_ParsesPortsLengthChecksumAndPayload()
    {
        var packet = PacketFixtures.CreateUdpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            sourcePort: 53000,
            destinationPort: 53,
            payload: [0x12, 0x34]);
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));

        var parsed = UdpDatagram.TryParse(ipv4Packet, out var datagram);

        Assert.True(parsed);
        Assert.Equal(53000, datagram.SourcePort);
        Assert.Equal(53, datagram.DestinationPort);
        Assert.Equal(10, datagram.Length);
        Assert.NotEqual(0, datagram.Checksum);
        Assert.Equal([0x12, 0x34], datagram.Payload.ToArray());
    }

    [Fact]
    public void TryParse_WithNonUdpPacket_ReturnsFalse()
    {
        var packet = PacketFixtures.CreateTcpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            sourcePort: 53000,
            destinationPort: 53,
            sequenceNumber: 1,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn);
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));

        Assert.False(UdpDatagram.TryParse(ipv4Packet, out _));
    }

    [Fact]
    public void TryParse_WithUdpLengthShorterThanHeader_ReturnsFalse()
    {
        var packet = PacketFixtures.CreateUdpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            sourcePort: 53000,
            destinationPort: 53,
            payload: [0x12, 0x34]);
        packet[24] = 0;
        packet[25] = 7;
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));

        Assert.False(UdpDatagram.TryParse(ipv4Packet, out _));
    }

    [Fact]
    public void TryParse_WithUdpLengthLongerThanAvailableBytes_ReturnsFalse()
    {
        var packet = PacketFixtures.CreateUdpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            sourcePort: 53000,
            destinationPort: 53,
            payload: [0x12, 0x34]);
        packet[24] = 0;
        packet[25] = 11;
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));

        Assert.False(UdpDatagram.TryParse(ipv4Packet, out _));
    }
}
