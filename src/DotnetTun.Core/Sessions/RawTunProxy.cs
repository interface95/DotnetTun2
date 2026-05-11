using DotnetTun.Abstractions;
using DotnetTun.Abstractions.Dns;
using DotnetTun.Abstractions.Routing;
using DotnetTun.Core.Dns;

namespace DotnetTun.Core.Sessions;

public sealed class RawTunProxy(TunPacketPump packetPump, IAsyncDisposable pipeline) : IAsyncDisposable
{
    private readonly TunPacketPump _packetPump = packetPump ?? throw new ArgumentNullException(nameof(packetPump));
    private readonly IAsyncDisposable _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));

    public static RawTunProxy Create(
        ITunDevice tunDevice,
        FakeIpPool fakeIpPool,
        IOutbound outbound,
        uint serverInitialSequence,
        int mtu = 1500,
        TimeSpan? responseReadTimeout = null,
        IDnsHijacker? dnsHijacker = null)
    {
        var pipeline = RawTcpTunPipeline.Create(fakeIpPool, outbound, serverInitialSequence, responseReadTimeout, dnsHijacker);
        return Create(tunDevice, pipeline, mtu);
    }

    public static RawTunProxy Create(
        ITunDevice tunDevice,
        IFakeIpStore fakeIpStore,
        IRouter router,
        IReadOnlyDictionary<string, IOutbound> outbounds,
        uint serverInitialSequence,
        int mtu = 1500,
        TimeSpan? responseReadTimeout = null,
        IDnsHijacker? dnsHijacker = null)
    {
        var pipeline = RawTcpTunPipeline.Create(fakeIpStore, router, outbounds, serverInitialSequence, responseReadTimeout, dnsHijacker);
        return Create(tunDevice, pipeline, mtu);
    }

    public ValueTask PumpOnceAsync(CancellationToken cancellationToken = default)
        => _packetPump.PumpOnceAsync(cancellationToken);

    public Task RunOpenAsync(CancellationToken cancellationToken = default)
        => _packetPump.RunOpenAsync(cancellationToken);

    public Task RunAsync(CancellationToken cancellationToken = default)
        => _packetPump.RunAsync(cancellationToken);

    public ValueTask DisposeAsync() => _pipeline.DisposeAsync();

    private static RawTunProxy Create(ITunDevice tunDevice, RawTcpTunPipeline pipeline, int mtu)
    {
        var packetPump = new TunPacketPump(tunDevice, pipeline.PacketHandler, mtu, pipeline.OutboundPackets);
        return new RawTunProxy(packetPump, pipeline);
    }
}
