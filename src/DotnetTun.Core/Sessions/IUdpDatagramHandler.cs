using DotnetTun.Core.Packets;

namespace DotnetTun.Core.Sessions;

public interface IUdpDatagramHandler
{
    ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(Ipv4Packet packet, UdpDatagram datagram, CancellationToken cancellationToken = default);
}
