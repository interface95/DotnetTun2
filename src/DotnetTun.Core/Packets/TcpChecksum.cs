using System.Buffers.Binary;

namespace DotnetTun.Core.Packets;

public static class TcpChecksum
{
    public static bool IsValid(Ipv4Packet packet, TcpSegment segment)
        => Compute(packet, segment.RawSegment.Span) == 0;

    public static ushort Compute(Ipv4Packet packet, ReadOnlySpan<byte> tcpSegment)
    {
        var sum = SumIpv4Address(packet.SourceAddressBits, 0);
        sum = SumIpv4Address(packet.DestinationAddressBits, sum);
        return Compute(sum, packet.Protocol, tcpSegment);
    }

    internal static ushort Compute(
        ReadOnlySpan<byte> sourceAddress,
        ReadOnlySpan<byte> destinationAddress,
        byte protocol,
        ReadOnlySpan<byte> tcpSegment)
    {
        if (sourceAddress.Length != 4)
        {
            throw new ArgumentException("TCP checksum requires a 4-byte IPv4 source address.", nameof(sourceAddress));
        }

        if (destinationAddress.Length != 4)
        {
            throw new ArgumentException("TCP checksum requires a 4-byte IPv4 destination address.", nameof(destinationAddress));
        }

        var sum = SumWords(sourceAddress, 0);
        sum = SumWords(destinationAddress, sum);
        return Compute(sum, protocol, tcpSegment);
    }

    private static ushort Compute(uint pseudoHeaderAddressSum, byte protocol, ReadOnlySpan<byte> tcpSegment)
    {
        var sum = pseudoHeaderAddressSum + protocol + checked((ushort)tcpSegment.Length);
        sum = SumWords(tcpSegment, sum);
        return Fold(sum);
    }

    private static uint SumIpv4Address(uint address, uint sum)
    {
        sum += address >> 16;
        sum += address & 0xFFFF;
        return sum;
    }

    private static ushort Fold(uint sum)
    {
        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    private static uint SumWords(ReadOnlySpan<byte> data, uint sum)
    {
        var index = 0;
        while (index + 1 < data.Length)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data[index..(index + 2)]);
            index += 2;
        }

        if (index < data.Length)
        {
            sum += (uint)(data[index] << 8);
        }

        return sum;
    }
}
