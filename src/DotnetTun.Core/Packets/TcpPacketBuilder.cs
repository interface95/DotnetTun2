using System.Buffers.Binary;
using System.Net;

namespace DotnetTun.Core.Packets;

public static class TcpPacketBuilder
{
    private const byte TcpProtocol = 6;
    private const int Ipv4HeaderLength = 20;
    private const int TcpHeaderLength = 20;
    private const byte DefaultTimeToLive = 64;
    private const ushort DefaultWindowSize = 65_535;

    public static byte[] Build(
        IPAddress sourceAddress,
        IPAddress destinationAddress,
        int sourcePort,
        int destinationPort,
        uint sequenceNumber,
        uint acknowledgmentNumber,
        TcpFlags flags,
        ReadOnlySpan<byte> payload = default)
    {
        Span<byte> sourceAddressBytes = stackalloc byte[4];
        if (!sourceAddress.TryWriteBytes(sourceAddressBytes, out var sourceBytesWritten) || sourceBytesWritten != 4)
        {
            throw new InvalidOperationException("TCP packet building requires an IPv4 source address.");
        }

        Span<byte> destinationAddressBytes = stackalloc byte[4];
        if (!destinationAddress.TryWriteBytes(destinationAddressBytes, out var destinationBytesWritten) || destinationBytesWritten != 4)
        {
            throw new InvalidOperationException("TCP packet building requires an IPv4 destination address.");
        }

        return Build(
            BinaryPrimitives.ReadUInt32BigEndian(sourceAddressBytes),
            BinaryPrimitives.ReadUInt32BigEndian(destinationAddressBytes),
            sourcePort,
            destinationPort,
            sequenceNumber,
            acknowledgmentNumber,
            flags,
            payload);
    }

    internal static byte[] Build(
        uint sourceAddress,
        uint destinationAddress,
        int sourcePort,
        int destinationPort,
        uint sequenceNumber,
        uint acknowledgmentNumber,
        TcpFlags flags,
        ReadOnlySpan<byte> payload = default)
    {
        var totalLength = Ipv4HeaderLength + TcpHeaderLength + payload.Length;
        var packet = new byte[totalLength];
        Span<byte> span = packet;

        span[0] = 0x45;
        span[1] = 0;
        BinaryPrimitives.WriteUInt16BigEndian(span[2..4], checked((ushort)totalLength));
        BinaryPrimitives.WriteUInt16BigEndian(span[4..6], 0);
        BinaryPrimitives.WriteUInt16BigEndian(span[6..8], 0x4000);
        span[8] = DefaultTimeToLive;
        span[9] = TcpProtocol;
        BinaryPrimitives.WriteUInt32BigEndian(span[12..16], sourceAddress);
        BinaryPrimitives.WriteUInt32BigEndian(span[16..20], destinationAddress);
        BinaryPrimitives.WriteUInt16BigEndian(span[10..12], InternetChecksum.Compute(span[..Ipv4HeaderLength]));

        var tcp = span[Ipv4HeaderLength..];
        BinaryPrimitives.WriteUInt16BigEndian(tcp[..2], checked((ushort)sourcePort));
        BinaryPrimitives.WriteUInt16BigEndian(tcp[2..4], checked((ushort)destinationPort));
        BinaryPrimitives.WriteUInt32BigEndian(tcp[4..8], sequenceNumber);
        BinaryPrimitives.WriteUInt32BigEndian(tcp[8..12], acknowledgmentNumber);
        tcp[12] = 0x50;
        tcp[13] = (byte)flags;
        BinaryPrimitives.WriteUInt16BigEndian(tcp[14..16], DefaultWindowSize);
        payload.CopyTo(tcp[TcpHeaderLength..]);

        BinaryPrimitives.WriteUInt16BigEndian(tcp[16..18], TcpChecksum.Compute(span[12..16], span[16..20], TcpProtocol, tcp));

        return packet;
    }
}
