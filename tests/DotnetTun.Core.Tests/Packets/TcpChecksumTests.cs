using DotnetTun.Core.Packets;
using Xunit;

namespace DotnetTun.Core.Tests.Packets;

public sealed class TcpChecksumTests
{
    [Fact]
    public void Compute_WithParsedTcpSegment_DoesNotAllocate()
    {
        var packet = PacketFixtures.CreateTcpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            sourcePort: 53000,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Psh | TcpFlags.Ack,
            payload: [0x12, 0x34, 0x56, 0x78]);
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));
        Assert.True(TcpSegment.TryParse(ipv4Packet, out var tcpSegment));
        Assert.Equal(0, TcpChecksum.Compute(ipv4Packet, tcpSegment.RawSegment.Span));

        var before = GC.GetAllocatedBytesForCurrentThread();
        ushort checksum = 0;

        for (var i = 0; i < 10; i++)
        {
            checksum |= TcpChecksum.Compute(ipv4Packet, tcpSegment.RawSegment.Span);
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, checksum);
        Assert.Equal(0, allocatedBytes);
    }
}
