using System.Buffers.Binary;
using System.Net;

namespace DotnetTun.Core.Packets;

public static class UdpPacketBuilder
{
    private const int Ipv4HeaderLength = 20;
    private const int UdpHeaderLength = 8;
    private const byte DefaultTimeToLive = 64;

    public static byte[] Build(
        IPAddress sourceAddress,
        IPAddress destinationAddress,
        int sourcePort,
        int destinationPort,
        ReadOnlySpan<byte> payload = default)
    {
        var udpLength = UdpHeaderLength + payload.Length;
        var totalLength = Ipv4HeaderLength + udpLength;
        var packet = new byte[totalLength];
        Span<byte> span = packet;

        span[0] = 0x45;
        span[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], checked((ushort)totalLength));
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 0x4000);
        span[8] = DefaultTimeToLive;
        span[9] = UdpDatagram.ProtocolNumber;
        if (!sourceAddress.TryWriteBytes(span[12..16], out var sourceBytesWritten) || sourceBytesWritten != 4)
        {
            throw new InvalidOperationException("UDP packet building requires an IPv4 source address.");
        }

        if (!destinationAddress.TryWriteBytes(span[16..20], out var destinationBytesWritten) || destinationBytesWritten != 4)
        {
            throw new InvalidOperationException("UDP packet building requires an IPv4 destination address.");
        }

        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], InternetChecksum.Compute(span[..Ipv4HeaderLength]));

        var udp = span[Ipv4HeaderLength..];
        BinaryPrimitives.WriteUInt16BigEndian(udp[..2], checked((ushort)sourcePort));
        BinaryPrimitives.WriteUInt16BigEndian(udp[2..4], checked((ushort)destinationPort));
        BinaryPrimitives.WriteUInt16BigEndian(udp[4..6], checked((ushort)udpLength));
        payload.CopyTo(udp[UdpHeaderLength..]);

        var ipv4Packet = new Ipv4Packet(
            packet,
            version: 4,
            headerLength: Ipv4HeaderLength,
            totalLength,
            UdpDatagram.ProtocolNumber,
            sourceAddress,
            destinationAddress);
        BinaryPrimitives.WriteUInt16BigEndian(udp[6..8], UdpChecksum.Compute(ipv4Packet, udp));

        return packet;
    }
}
