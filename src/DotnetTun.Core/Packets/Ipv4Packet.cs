using System.Buffers.Binary;
using System.Net;

namespace DotnetTun.Core.Packets;

public readonly record struct Ipv4Packet(
    ReadOnlyMemory<byte> RawPacket,
    int Version,
    int HeaderLength,
    int TotalLength,
    byte Protocol,
    IPAddress SourceAddress,
    IPAddress DestinationAddress)
{
    private const ushort MoreFragmentsFlag = 0x2000;
    private const ushort FragmentOffsetMask = 0x1FFF;
    private const int MinimumHeaderLength = 20;

    public static bool TryParse(ReadOnlyMemory<byte> packet, out Ipv4Packet ipv4Packet)
    {
        var span = packet.Span;
        if (span.Length < MinimumHeaderLength)
        {
            ipv4Packet = default;
            return false;
        }

        var version = span[0] >> 4;
        if (version != 4)
        {
            ipv4Packet = default;
            return false;
        }

        var headerLength = (span[0] & 0x0F) * 4;
        if (headerLength < MinimumHeaderLength || headerLength > span.Length)
        {
            ipv4Packet = default;
            return false;
        }

        var totalLength = BinaryPrimitives.ReadUInt16BigEndian(span[2..4]);
        if (totalLength < headerLength || totalLength > span.Length)
        {
            ipv4Packet = default;
            return false;
        }

        var flagsAndFragmentOffset = BinaryPrimitives.ReadUInt16BigEndian(span[6..8]);
        if ((flagsAndFragmentOffset & MoreFragmentsFlag) != 0 || (flagsAndFragmentOffset & FragmentOffsetMask) != 0)
        {
            ipv4Packet = default;
            return false;
        }

        if (!InternetChecksum.IsValid(span[..headerLength]))
        {
            ipv4Packet = default;
            return false;
        }

        ipv4Packet = new Ipv4Packet(
            packet[..totalLength],
            version,
            headerLength,
            totalLength,
            span[9],
            new IPAddress(span[12..16]),
            new IPAddress(span[16..20]));
        return true;
    }
}
