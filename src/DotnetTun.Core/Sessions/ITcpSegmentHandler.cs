using DotnetTun.Core.Packets;

namespace DotnetTun.Core.Sessions;

public interface ITcpSegmentHandler
{
    ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(Ipv4Packet packet, TcpSegment segment, CancellationToken cancellationToken = default);
}
