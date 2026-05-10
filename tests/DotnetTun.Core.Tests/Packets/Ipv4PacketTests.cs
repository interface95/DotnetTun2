using System.Buffers.Binary;
using System.Net;
using DotnetTun.Core.Packets;
using Xunit;

namespace DotnetTun.Core.Tests.Packets;

public sealed class Ipv4PacketTests
{
    [Fact]
    public void TryParse_WithValidIpv4Packet_ReturnsHeaderMetadata()
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

        // Act
        var parsed = Ipv4Packet.TryParse(packet, out var ipv4Packet);

        // Assert
        Assert.True(parsed);
        Assert.Equal(4, ipv4Packet.Version);
        Assert.Equal(20, ipv4Packet.HeaderLength);
        Assert.Equal(40, ipv4Packet.TotalLength);
        Assert.Equal(6, ipv4Packet.Protocol);
        Assert.Equal(IPAddress.Parse("10.0.0.1"), ipv4Packet.SourceAddress);
        Assert.Equal(IPAddress.Parse("198.18.0.1"), ipv4Packet.DestinationAddress);
        Assert.Equal(packet, ipv4Packet.RawPacket.ToArray());
    }

    [Fact]
    public void TryParse_WithMoreFragmentsFlag_ReturnsFalse()
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
        packet[6] |= 0x20;
        RecomputeIpv4HeaderChecksum(packet);

        // Act
        var parsed = Ipv4Packet.TryParse(packet, out _);

        // Assert
        Assert.False(parsed);
    }

    [Fact]
    public void TryParse_WithFragmentOffset_ReturnsFalse()
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
        packet[7] = 0x01;
        RecomputeIpv4HeaderChecksum(packet);

        // Act
        var parsed = Ipv4Packet.TryParse(packet, out _);

        // Assert
        Assert.False(parsed);
    }

    [Fact]
    public void TryParse_WithInvalidHeaderChecksum_ReturnsFalse()
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
        packet[10] ^= 0xFF;

        // Act
        var parsed = Ipv4Packet.TryParse(packet, out _);

        // Assert
        Assert.False(parsed);
    }

    private static void RecomputeIpv4HeaderChecksum(byte[] packet)
    {
        var headerLength = (packet[0] & 0x0F) * 4;
        packet[10] = 0;
        packet[11] = 0;
        var checksum = InternetChecksum.Compute(packet.AsSpan(0, headerLength));
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(10, 2), checksum);
    }
}
