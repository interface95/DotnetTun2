using DotnetTun.Platforms.MacOS.Networking;
using Xunit;

namespace DotnetTun.Platforms.MacOS.Tests.Networking;

public sealed class MacUtunFrameTests
{
    [Fact]
    public void TryWriteFrame_WithIpv4Packet_WritesAddressFamilyHeaderWithoutAllocating()
    {
        ReadOnlySpan<byte> packet = [0x45, 0x00, 0x00, 0x14];
        Span<byte> framedPacket = stackalloc byte[MacUtunFrame.HeaderLength + packet.Length];
        Assert.True(MacUtunFrame.TryWriteFrame(packet, framedPacket, out var bytesWritten));
        Assert.Equal(framedPacket.Length, bytesWritten);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var totalBytesWritten = 0;

        for (var i = 0; i < 10; i++)
        {
            Assert.True(MacUtunFrame.TryWriteFrame(packet, framedPacket, out bytesWritten));
            totalBytesWritten += bytesWritten;
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(framedPacket.Length * 10, totalBytesWritten);
        Assert.Equal([0x00, 0x00, 0x00, 0x02, .. packet], framedPacket.ToArray());
        Assert.Equal(0, allocatedBytes);
    }

    [Fact]
    public void TryReadPayload_WithIpv6Frame_StripsAddressFamilyHeaderWithoutAllocating()
    {
        ReadOnlySpan<byte> packet = [0x60, 0x00, 0x00, 0x00];
        ReadOnlySpan<byte> framedPacket = [0x00, 0x00, 0x00, 0x1e, .. packet];
        Span<byte> destination = stackalloc byte[packet.Length];
        Assert.True(MacUtunFrame.TryReadPayload(framedPacket, destination, out var payloadLength));
        Assert.Equal(packet.Length, payloadLength);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var totalPayloadLength = 0;

        for (var i = 0; i < 10; i++)
        {
            Assert.True(MacUtunFrame.TryReadPayload(framedPacket, destination, out payloadLength));
            totalPayloadLength += payloadLength;
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(packet.Length * 10, totalPayloadLength);
        Assert.Equal(packet.ToArray(), destination.ToArray());
        Assert.Equal(0, allocatedBytes);
    }
}
