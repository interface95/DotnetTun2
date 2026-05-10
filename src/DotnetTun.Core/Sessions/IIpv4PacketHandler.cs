using DotnetTun.Core.Packets;

namespace DotnetTun.Core.Sessions;

public interface IIpv4PacketHandler
{
    ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(Ipv4Packet packet, CancellationToken cancellationToken = default);
}
