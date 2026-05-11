using System.Buffers.Binary;
using System.Net;

namespace DotnetTun.Core.Packets;

public readonly record struct Ipv4Packet
{
    private const ushort MoreFragmentsFlag = 0x2000;
    private const ushort FragmentOffsetMask = 0x1FFF;
    private const int MinimumHeaderLength = 20;

    private readonly uint _sourceAddress;
    private readonly uint _destinationAddress;

    public Ipv4Packet(
        ReadOnlyMemory<byte> rawPacket,
        int version,
        int headerLength,
        int totalLength,
        byte protocol,
        IPAddress sourceAddress,
        IPAddress destinationAddress)
        : this(
            rawPacket,
            version,
            headerLength,
            totalLength,
            protocol,
            ReadIpv4Address(sourceAddress, nameof(sourceAddress)),
            ReadIpv4Address(destinationAddress, nameof(destinationAddress)))
    {
    }

    private Ipv4Packet(
        ReadOnlyMemory<byte> rawPacket,
        int version,
        int headerLength,
        int totalLength,
        byte protocol,
        uint sourceAddress,
        uint destinationAddress)
    {
        RawPacket = rawPacket;
        Version = version;
        HeaderLength = headerLength;
        TotalLength = totalLength;
        Protocol = protocol;
        _sourceAddress = sourceAddress;
        _destinationAddress = destinationAddress;
    }

    public ReadOnlyMemory<byte> RawPacket { get; }

    public int Version { get; }

    public int HeaderLength { get; }

    public int TotalLength { get; }

    public byte Protocol { get; }

    public IPAddress SourceAddress => CreateAddress(_sourceAddress);

    public IPAddress DestinationAddress => CreateAddress(_destinationAddress);

    internal uint SourceAddressBits => _sourceAddress;

    internal uint DestinationAddressBits => _destinationAddress;

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
            BinaryPrimitives.ReadUInt32BigEndian(span[12..16]),
            BinaryPrimitives.ReadUInt32BigEndian(span[16..20]));
        return true;
    }

    internal void WriteSourceAddress(Span<byte> destination)
        => BinaryPrimitives.WriteUInt32BigEndian(destination, _sourceAddress);

    internal void WriteDestinationAddress(Span<byte> destination)
        => BinaryPrimitives.WriteUInt32BigEndian(destination, _destinationAddress);

    private static uint ReadIpv4Address(IPAddress address, string parameterName)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out var bytesWritten) || bytesWritten != 4)
        {
            throw new ArgumentException("IPv4 packet metadata requires a 4-byte IPv4 address.", parameterName);
        }

        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static IPAddress CreateAddress(uint address)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, address);
        return new IPAddress(bytes);
    }
}
