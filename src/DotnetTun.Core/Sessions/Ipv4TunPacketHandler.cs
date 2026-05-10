using DotnetTun.Core.Packets;

namespace DotnetTun.Core.Sessions;

public sealed class Ipv4TunPacketHandler(IIpv4PacketHandler handler) : ITunPacketHandler
{
    private readonly IIpv4PacketHandler _handler = handler ?? throw new ArgumentNullException(nameof(handler));

    public ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        if (!Ipv4Packet.TryParse(packet, out Ipv4Packet ipv4Packet))
        {
            IReadOnlyList<ReadOnlyMemory<byte>> responses = [];
            return ValueTask.FromResult(responses);
        }

        return _handler.HandleAsync(ipv4Packet, cancellationToken);
    }
}
