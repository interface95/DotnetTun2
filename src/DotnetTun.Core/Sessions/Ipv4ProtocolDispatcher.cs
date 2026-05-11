using DotnetTun.Core.Packets;

namespace DotnetTun.Core.Sessions;

public sealed class Ipv4ProtocolDispatcher(IIpv4PacketHandler tcpHandler, IIpv4PacketHandler? udpHandler = null) : IIpv4PacketHandler
{
    private const byte TcpProtocol = 6;

    private readonly IIpv4PacketHandler _tcpHandler = tcpHandler ?? throw new ArgumentNullException(nameof(tcpHandler));
    private readonly IIpv4PacketHandler? _udpHandler = udpHandler;

    public ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(Ipv4Packet packet, CancellationToken cancellationToken = default)
    {
        return packet.Protocol switch
        {
            TcpProtocol => _tcpHandler.HandleAsync(packet, cancellationToken),
            UdpDatagram.ProtocolNumber when _udpHandler is not null => _udpHandler.HandleAsync(packet, cancellationToken),
            _ => ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<byte>>>([]),
        };
    }
}
