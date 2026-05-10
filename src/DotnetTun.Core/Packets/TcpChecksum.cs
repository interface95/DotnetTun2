using System.Buffers.Binary;

namespace DotnetTun.Core.Packets;

public static class TcpChecksum
{
    public static bool IsValid(Ipv4Packet packet, TcpSegment segment)
        => Compute(packet, segment.RawSegment.Span) == 0;

    public static ushort Compute(Ipv4Packet packet, ReadOnlySpan<byte> tcpSegment)
    {
        byte[] pseudoPacket = new byte[12 + tcpSegment.Length];
        Span<byte> pseudo = pseudoPacket;

        packet.SourceAddress.GetAddressBytes().CopyTo(pseudo[..4]);
        packet.DestinationAddress.GetAddressBytes().CopyTo(pseudo[4..8]);
        pseudo[8] = 0;
        pseudo[9] = packet.Protocol;
        BinaryPrimitives.WriteUInt16BigEndian(pseudo[10..12], checked((ushort)tcpSegment.Length));
        tcpSegment.CopyTo(pseudo[12..]);

        return InternetChecksum.Compute(pseudo);
    }
}
