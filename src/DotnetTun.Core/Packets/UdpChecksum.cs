using System.Buffers.Binary;

namespace DotnetTun.Core.Packets;

public static class UdpChecksum
{
    public static bool IsValid(Ipv4Packet packet, UdpDatagram datagram)
        => datagram.Checksum == 0 || Compute(packet, datagram.RawDatagram.Span) == 0;

    public static ushort Compute(Ipv4Packet packet, ReadOnlySpan<byte> udpDatagram)
    {
        var sum = SumIpv4Address(packet.SourceAddressBits, 0);
        sum = SumIpv4Address(packet.DestinationAddressBits, sum);
        sum += UdpDatagram.ProtocolNumber;
        sum += checked((ushort)udpDatagram.Length);
        sum = SumWords(udpDatagram, sum);

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    private static uint SumIpv4Address(uint address, uint sum)
    {
        sum += address >> 16;
        sum += address & 0xFFFF;
        return sum;
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
