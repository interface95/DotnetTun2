using DotnetTun.Abstractions;
using DotnetTun.Abstractions.Dns;
using DotnetTun.Abstractions.Routing;
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
        TimeSpan? responseReadTimeout = null,
        IDnsHijacker? dnsHijacker = null)
        => CreateCore(
            (sessions, outboundPackets) => new OutboundTcpPayloadSink(
                fakeIpPool,
                outbound,
                responseReadTimeout,
                remotePayloadHandler: (session, payload, cancellationToken) => QueueRemotePayloadAsync(sessions, outboundPackets, session, payload, cancellationToken)),
            serverInitialSequence,
            dnsHijacker);

    public static RawTcpTunPipeline Create(
        IFakeIpStore fakeIpStore,
        IRouter router,
        IReadOnlyDictionary<string, IOutbound> outbounds,
        uint serverInitialSequence,
        TimeSpan? responseReadTimeout = null,
        IDnsHijacker? dnsHijacker = null)
        => CreateCore(
            (sessions, outboundPackets) => new OutboundTcpPayloadSink(
                fakeIpStore,
                router,
                outbounds,
                responseReadTimeout,
                remotePayloadHandler: (session, payload, cancellationToken) => QueueRemotePayloadAsync(sessions, outboundPackets, session, payload, cancellationToken)),
            serverInitialSequence,
            dnsHijacker);

    public async ValueTask DisposeAsync()
    {
        if (_disposable is not null)
        {
            await _disposable.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static RawTcpTunPipeline CreateCore(
        Func<TcpSessionTable, TunOutboundPacketQueue, OutboundTcpPayloadSink> createPayloadSink,
        uint serverInitialSequence,
        IDnsHijacker? dnsHijacker)
    {
        var sessions = new TcpSessionTable();
        var outboundPackets = new TunOutboundPacketQueue();
        var payloadSink = createPayloadSink(sessions, outboundPackets);
        var rawTcpHandler = new RawTcpSessionHandler(sessions, serverInitialSequence, payloadSink);
        var tcpHandler = new TcpIpv4PacketHandler(rawTcpHandler);
        IIpv4PacketHandler ipv4Handler = tcpHandler;
        if (dnsHijacker is not null)
        {
            var udpHandler = new UdpIpv4PacketHandler(new Dns53Sink(dnsHijacker));
            ipv4Handler = new Ipv4ProtocolDispatcher(tcpHandler, udpHandler);
        }

        var packetHandler = new Ipv4TunPacketHandler(ipv4Handler);
        return new RawTcpTunPipeline(packetHandler, outboundPackets, payloadSink);
    }

    private static async ValueTask QueueRemotePayloadAsync(
        TcpSessionTable sessions,
        TunOutboundPacketQueue outboundPackets,
        TcpSession session,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        if (!sessions.TryGet(session.Key, out var currentSession) || currentSession is null || currentSession.Value.State != TcpSessionState.Established)
        {
            return;
        }

        var establishedSession = currentSession.Value;

        var serverPayloadPacket = TcpPacketBuilder.Build(
            establishedSession.Key.DestinationAddressBits,
            establishedSession.Key.SourceAddressBits,
            establishedSession.Key.DestinationPort,
            establishedSession.Key.SourcePort,
            establishedSession.NextServerSequence,
            establishedSession.NextClientSequence,
            TcpFlags.Psh | TcpFlags.Ack,
            payload.Span);

        var nextServerSequence = establishedSession.NextServerSequence + (uint)payload.Length;
        if (sessions.TryAdvanceServerSequence(establishedSession.Key, nextServerSequence, out _))
        {
            await outboundPackets.WriteAsync(serverPayloadPacket, cancellationToken).ConfigureAwait(false);
        }
    }
}
