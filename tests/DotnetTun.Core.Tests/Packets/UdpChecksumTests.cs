using DotnetTun.Core.Packets;
using DotnetTun.Core.Tests.Packets;
using Xunit;

namespace DotnetTun.Core.Tests.Packets;

public sealed class UdpChecksumTests
{
    [Fact]
    public void IsValid_WithPacketBuiltByUdpPacketBuilder_ReturnsTrue()
    {
        var packet = PacketFixtures.CreateUdpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            sourcePort: 53000,
            destinationPort: 53,
            payload: [0x12, 0x34]);
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));
        Assert.True(UdpDatagram.TryParse(ipv4Packet, out var datagram));

        Assert.True(UdpChecksum.IsValid(ipv4Packet, datagram));
    }

    [Fact]
    public void IsValid_WhenPayloadIsModified_ReturnsFalse()
    {
        var packet = PacketFixtures.CreateUdpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            sourcePort: 53000,
            destinationPort: 53,
            payload: [0x12, 0x34]);
        packet[^1] ^= 0xFF;
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));
        Assert.True(UdpDatagram.TryParse(ipv4Packet, out var datagram));

        Assert.False(UdpChecksum.IsValid(ipv4Packet, datagram));
    }

    [Fact]
    public void Compute_WithParsedUdpDatagram_DoesNotAllocate()
    {
        var packet = PacketFixtures.CreateUdpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            sourcePort: 53000,
            destinationPort: 53,
            payload: [0x12, 0x34]);
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));
        Assert.True(UdpDatagram.TryParse(ipv4Packet, out var datagram));
        Assert.Equal(0, UdpChecksum.Compute(ipv4Packet, datagram.RawDatagram.Span));

        var before = GC.GetAllocatedBytesForCurrentThread();
        ushort checksum = 0;

        for (var i = 0; i < 10; i++)
        {
            checksum |= UdpChecksum.Compute(ipv4Packet, datagram.RawDatagram.Span);
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, checksum);
        Assert.Equal(0, allocatedBytes);
    }
}
