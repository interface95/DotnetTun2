using DotnetTun.Core.Packets;

namespace DotnetTun.Core.Sessions;

public sealed class UdpIpv4PacketHandler(IUdpDatagramHandler handler) : IIpv4PacketHandler
{
    private readonly IUdpDatagramHandler _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(Ipv4Packet packet, CancellationToken cancellationToken = default)
    {
        if (!UdpDatagram.TryParse(packet, out var datagram) || !UdpChecksum.IsValid(packet, datagram))
        {
            IReadOnlyList<ReadOnlyMemory<byte>> responses = [];
            return ValueTask.FromResult(responses);
        }

        return _handler.HandleAsync(packet, datagram, cancellationToken);
    }
}
