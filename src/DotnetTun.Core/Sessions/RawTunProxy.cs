using DotnetTun.Abstractions;
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
        TimeSpan? responseReadTimeout = null)
    {
        RawTcpTunPipeline pipeline = RawTcpTunPipeline.Create(fakeIpPool, outbound, serverInitialSequence, responseReadTimeout);
        var packetPump = new TunPacketPump(tunDevice, pipeline.PacketHandler, mtu, pipeline.OutboundPackets);
        return new RawTunProxy(packetPump, pipeline);
    }

    public ValueTask PumpOnceAsync(int fileDescriptor, CancellationToken cancellationToken = default)
        => _packetPump.PumpOnceAsync(fileDescriptor, cancellationToken);

    public Task RunOpenAsync(int fileDescriptor, CancellationToken cancellationToken = default)
        => _packetPump.RunOpenAsync(fileDescriptor, cancellationToken);

    public Task RunAsync(CancellationToken cancellationToken = default)
        => _packetPump.RunAsync(cancellationToken);

    public ValueTask DisposeAsync() => _pipeline.DisposeAsync();
}
