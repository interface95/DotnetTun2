using System.Net;
using DotnetTun.Core.Packets;
using Xunit;

namespace DotnetTun.Core.Tests.Packets;

public sealed class UdpPacketBuilderTests
{
    [Fact]
    public void Build_WithPayload_OnlyAllocatesReturnedPacket()
    {
        var sourceAddress = IPAddress.Parse("10.0.0.1");
        var destinationAddress = IPAddress.Parse("198.18.0.1");
        ReadOnlySpan<byte> payload = [0x12, 0x34, 0x56, 0x78];
        var expectedPacketLength = 32;

        var warmupPacket = BuildPacket(sourceAddress, destinationAddress, payload);
        Assert.Equal(expectedPacketLength, warmupPacket.Length);

        var requiredAllocationBefore = GC.GetAllocatedBytesForCurrentThread();
        var requiredPacket = new byte[expectedPacketLength];
        requiredPacket[0] = 0x45;
        var requiredAllocation = GC.GetAllocatedBytesForCurrentThread() - requiredAllocationBefore;

        var before = GC.GetAllocatedBytesForCurrentThread();
        var packet = BuildPacket(sourceAddress, destinationAddress, payload);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(expectedPacketLength, packet.Length);
        Assert.Equal(requiredAllocation, allocatedBytes);
    }

    private static byte[] BuildPacket(IPAddress sourceAddress, IPAddress destinationAddress, ReadOnlySpan<byte> payload)
        => UdpPacketBuilder.Build(
            sourceAddress,
            destinationAddress,
            sourcePort: 53000,
            destinationPort: 53,
            payload: payload);
}
