using System.Buffers.Binary;
using System.Net;

namespace DotnetTun.Core.Sessions;

public readonly record struct TcpFlowKey
{
    private readonly uint _sourceAddress;
    private readonly uint _destinationAddress;

    public TcpFlowKey(IPAddress sourceAddress, int sourcePort, IPAddress destinationAddress, int destinationPort)
        : this(ReadIpv4Address(sourceAddress, nameof(sourceAddress)), sourcePort, ReadIpv4Address(destinationAddress, nameof(destinationAddress)), destinationPort)
    {
    }

    internal TcpFlowKey(uint sourceAddress, int sourcePort, uint destinationAddress, int destinationPort)
    {
        _sourceAddress = sourceAddress;
        SourcePort = sourcePort;
        _destinationAddress = destinationAddress;
        DestinationPort = destinationPort;
    }

    public IPAddress SourceAddress => CreateAddress(_sourceAddress);

    public int SourcePort { get; }

    public IPAddress DestinationAddress => CreateAddress(_destinationAddress);

    public int DestinationPort { get; }

    internal uint SourceAddressBits => _sourceAddress;

    internal uint DestinationAddressBits => _destinationAddress;

    private static uint ReadIpv4Address(IPAddress address, string parameterName)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out var bytesWritten) || bytesWritten != 4)
        {
            throw new ArgumentException("TCP flow keys require IPv4 addresses.", parameterName);
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
