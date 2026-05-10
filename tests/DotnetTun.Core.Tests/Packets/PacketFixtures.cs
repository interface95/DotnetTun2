using System.Net;
using DotnetTun.Core.Packets;

namespace DotnetTun.Core.Tests.Packets;

internal static class PacketFixtures
{
    public static byte[] CreateTcpPacket(
        byte[] sourceAddress,
        byte[] destinationAddress,
        int sourcePort,
        int destinationPort,
        uint sequenceNumber,
        uint acknowledgmentNumber,
        TcpFlags flags,
        ReadOnlySpan<byte> payload = default)
        => TcpPacketBuilder.Build(
            new IPAddress(sourceAddress),
            new IPAddress(destinationAddress),
            sourcePort,
            destinationPort,
            sequenceNumber,
            acknowledgmentNumber,
            flags,
            payload);
}
