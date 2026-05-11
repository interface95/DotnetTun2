using System.Buffers.Binary;

namespace DotnetTun.Core.Packets;

public readonly record struct UdpDatagram(
    ReadOnlyMemory<byte> RawDatagram,
    int SourcePort,
    int DestinationPort,
    int Length,
    ushort Checksum,
    ReadOnlyMemory<byte> Payload)
{
    public const byte ProtocolNumber = 17;
    private const int HeaderLength = 8;

    public static bool TryParse(Ipv4Packet packet, out UdpDatagram datagram)
    {
        datagram = default;
        if (packet.Protocol != ProtocolNumber)
        {
            return false;
        }

        var rawPacket = packet.RawPacket;
        var udpOffset = packet.HeaderLength;
        var availableLength = packet.TotalLength - udpOffset;
        if (availableLength < HeaderLength)
        {
            return false;
        }

        var udp = rawPacket.Span[udpOffset..packet.TotalLength];
        var udpLength = BinaryPrimitives.ReadUInt16BigEndian(udp[4..6]);
        if (udpLength < HeaderLength || udpLength > availableLength)
        {
            return false;
        }

        var payloadOffset = udpOffset + HeaderLength;
        var payloadLength = udpLength - HeaderLength;
        datagram = new UdpDatagram(
            rawPacket[udpOffset..(udpOffset + udpLength)],
            BinaryPrimitives.ReadUInt16BigEndian(udp[..2]),
            BinaryPrimitives.ReadUInt16BigEndian(udp[2..4]),
            udpLength,
            BinaryPrimitives.ReadUInt16BigEndian(udp[6..8]),
            rawPacket[payloadOffset..(payloadOffset + payloadLength)]);
        return true;
    }
}
