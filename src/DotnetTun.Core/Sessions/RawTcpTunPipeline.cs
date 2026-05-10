using DotnetTun.Abstractions;
using DotnetTun.Core.Dns;
using DotnetTun.Core.Packets;

namespace DotnetTun.Core.Sessions;

public sealed class RawTcpTunPipeline(
    ITunPacketHandler packetHandler,
    TunOutboundPacketQueue? outboundPackets = null,
    IAsyncDisposable? disposable = null) : IAsyncDisposable
{
    private readonly IAsyncDisposable? _disposable = disposable;

    public ITunPacketHandler PacketHandler { get; } = packetHandler ?? throw new ArgumentNullException(nameof(packetHandler));

    public TunOutboundPacketQueue OutboundPackets { get; } = outboundPackets ?? new TunOutboundPacketQueue();

    public static RawTcpTunPipeline Create(
        FakeIpPool fakeIpPool,
        IOutbound outbound,
        uint serverInitialSequence,
        TimeSpan? responseReadTimeout = null)
    {
        var sessions = new TcpSessionTable();
        var outboundPackets = new TunOutboundPacketQueue();
        var payloadSink = new OutboundTcpPayloadSink(
            fakeIpPool,
            outbound,
            responseReadTimeout,
            remotePayloadHandler: (session, payload, cancellationToken) => QueueRemotePayloadAsync(sessions, outboundPackets, session, payload, cancellationToken));
        var rawTcpHandler = new RawTcpSessionHandler(sessions, serverInitialSequence, payloadSink);
        var tcpHandler = new TcpIpv4PacketHandler(rawTcpHandler);
        var packetHandler = new Ipv4TunPacketHandler(tcpHandler);
        return new RawTcpTunPipeline(packetHandler, outboundPackets, payloadSink);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposable is not null)
        {
            await _disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async ValueTask QueueRemotePayloadAsync(
        TcpSessionTable sessions,
        TunOutboundPacketQueue outboundPackets,
        TcpSession session,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (!sessions.TryGet(session.Key, out TcpSession? currentSession) || currentSession is null || currentSession.State != TcpSessionState.Established)
        {
            return;
        }

        byte[] serverPayloadPacket = TcpPacketBuilder.Build(
            currentSession.Key.DestinationAddress,
            currentSession.Key.SourceAddress,
            currentSession.Key.DestinationPort,
            currentSession.Key.SourcePort,
            currentSession.NextServerSequence,
            currentSession.NextClientSequence,
            TcpFlags.Psh | TcpFlags.Ack,
            payload.Span);

        uint nextServerSequence = currentSession.NextServerSequence + (uint)payload.Length;
        if (sessions.TryAdvanceServerSequence(currentSession.Key, nextServerSequence, out _))
        {
            await outboundPackets.WriteAsync(serverPayloadPacket, cancellationToken).ConfigureAwait(false);
        }
    }
}
