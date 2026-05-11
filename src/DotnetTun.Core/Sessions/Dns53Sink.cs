using DotnetTun.Abstractions.Dns;
using DotnetTun.Core.Packets;

namespace DotnetTun.Core.Sessions;

public sealed class Dns53Sink(IDnsHijacker hijacker) : IUdpDatagramHandler
{
    private const int DnsPort = 53;

    private readonly IDnsHijacker _hijacker = hijacker ?? throw new ArgumentNullException(nameof(hijacker));

    public async ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(Ipv4Packet packet, UdpDatagram datagram, CancellationToken cancellationToken = default)
    {
        if (datagram.DestinationPort != DnsPort)
        {
            return [];
        }

        var result = await _hijacker.HandleAsync(datagram.Payload, cancellationToken).ConfigureAwait(false);
        if (result.Response is null)
        {
            return [];
        }

        var responsePacket = UdpPacketBuilder.Build(
            packet.DestinationAddress,
            packet.SourceAddress,
            datagram.DestinationPort,
            datagram.SourcePort,
            result.Response);
        return [responsePacket];
    }
}
