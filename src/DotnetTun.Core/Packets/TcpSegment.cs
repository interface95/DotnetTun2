using System.Buffers.Binary;

namespace DotnetTun.Core.Packets;

public readonly record struct TcpSegment(
    ReadOnlyMemory<byte> RawSegment,
    int SourcePort,
    int DestinationPort,
    uint SequenceNumber,
    uint AcknowledgmentNumber,
    int HeaderLength,
    TcpFlags Flags,
    ushort WindowSize,
    ReadOnlyMemory<byte> Payload)
{
    private const byte TcpProtocol = 6;
    private const int MinimumHeaderLength = 20;

    public bool IsSyn => Flags.HasFlag(TcpFlags.Syn);

    public bool IsAck => Flags.HasFlag(TcpFlags.Ack);

    public bool IsFin => Flags.HasFlag(TcpFlags.Fin);

    public bool IsRst => Flags.HasFlag(TcpFlags.Rst);

    public static bool TryParse(Ipv4Packet packet, out TcpSegment tcpSegment)
    {
        if (packet.Protocol != TcpProtocol)
        {
            tcpSegment = default;
            return false;
        }

        ReadOnlyMemory<byte> rawPacket = packet.RawPacket;
        int tcpOffset = packet.HeaderLength;
        int availableLength = packet.TotalLength - tcpOffset;
        if (availableLength < MinimumHeaderLength)
        {
            tcpSegment = default;
            return false;
        }

        ReadOnlySpan<byte> tcpHeader = rawPacket.Span[tcpOffset..packet.TotalLength];
        int headerLength = (tcpHeader[12] >> 4) * 4;
        if (headerLength < MinimumHeaderLength || headerLength > availableLength)
        {
            tcpSegment = default;
            return false;
        }

        int payloadOffset = tcpOffset + headerLength;
        tcpSegment = new TcpSegment(
            rawPacket[tcpOffset..packet.TotalLength],
            BinaryPrimitives.ReadUInt16BigEndian(tcpHeader[..2]),
            BinaryPrimitives.ReadUInt16BigEndian(tcpHeader[2..4]),
            BinaryPrimitives.ReadUInt32BigEndian(tcpHeader[4..8]),
            BinaryPrimitives.ReadUInt32BigEndian(tcpHeader[8..12]),
            headerLength,
            (TcpFlags)tcpHeader[13],
            BinaryPrimitives.ReadUInt16BigEndian(tcpHeader[14..16]),
            rawPacket[payloadOffset..packet.TotalLength]);
        return true;
    }
}
