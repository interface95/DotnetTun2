using DotnetTun.Core.Packets;

namespace DotnetTun.Core.Sessions;

public sealed class TcpIpv4PacketHandler(ITcpSegmentHandler handler) : IIpv4PacketHandler
{
    private readonly ITcpSegmentHandler _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(Ipv4Packet packet, CancellationToken cancellationToken = default)
    {
        if (!TcpSegment.TryParse(packet, out var segment) || !TcpChecksum.IsValid(packet, segment))
        {
            IReadOnlyList<ReadOnlyMemory<byte>> responses = [];
            return ValueTask.FromResult(responses);
        }

        return _handler.HandleAsync(packet, segment, cancellationToken);
    }
}
