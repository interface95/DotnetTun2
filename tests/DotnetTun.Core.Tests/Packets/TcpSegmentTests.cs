using DotnetTun.Core.Packets;
using Xunit;

namespace DotnetTun.Core.Tests.Packets;

public sealed class TcpSegmentTests
{
    [Fact]
    public void TryParse_WithValidTcpIpv4Packet_ReturnsPortsAndFlags()
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
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));

        // Act
        var parsed = TcpSegment.TryParse(ipv4Packet, out var tcpSegment);

        // Assert
        Assert.True(parsed);
        Assert.Equal(54321, tcpSegment.SourcePort);
        Assert.Equal(443, tcpSegment.DestinationPort);
        Assert.Equal(20, tcpSegment.HeaderLength);
        Assert.Equal(TcpFlags.Syn, tcpSegment.Flags);
        Assert.True(tcpSegment.IsSyn);
        Assert.False(tcpSegment.IsAck);
        Assert.Empty(tcpSegment.Payload.ToArray());
    }
}
