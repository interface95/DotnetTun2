using System.Net;
using DotnetTun.Abstractions;
using DotnetTun.Abstractions.Dns;
using DotnetTun.Abstractions.Routing;
using DotnetTun.Core.Dns;
using DotnetTun.Core.Packets;
using DotnetTun.Core.Tests.Packets;
using DotnetTun.Core.Sessions;
using Xunit;

namespace DotnetTun.Core.Tests.Sessions;

public sealed class RawTcpTunPipelineTests
{
    [Fact]
    public async Task CreateHandler_WithEstablishedPayload_WritesPayloadToOutboundAndReturnsAck()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");
        var outbound = new RecordingOutbound();
        await using RawTcpTunPipeline pipeline = RawTcpTunPipeline.Create(pool, outbound, serverInitialSequence: 9_000);
        ITunPacketHandler handler = pipeline.PacketHandler;

        await HandlePacketAsync(handler, PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: lease.FakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_000,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn));

        await HandlePacketAsync(handler, PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: lease.FakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Ack));

        byte[] payload = [0x42];

        // Act
        IReadOnlyList<ReadOnlyMemory<byte>> responses = await HandlePacketAsync(handler, PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: lease.FakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Psh | TcpFlags.Ack,
            payload: payload));

        // Assert
        Assert.Equal(payload, outbound.Stream.WrittenBytes.ToArray());
        Assert.Single(responses);
    }

    [Fact]
    public async Task CreateHandler_WithDnsHijacker_HandlesUdp53Query()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var outbound = new RecordingOutbound();
        var hijacker = new StubDnsHijacker(DnsHandlingResult.Intercepted([0xAB, 0xCD]));
        await using RawTcpTunPipeline pipeline = RawTcpTunPipeline.Create(
            pool,
            outbound,
            serverInitialSequence: 9_000,
            dnsHijacker: hijacker);
        ITunPacketHandler handler = pipeline.PacketHandler;
        var packet = PacketFixtures.CreateUdpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 53000,
            destinationPort: 53,
            payload: [0x12, 0x34]);

        // Act
        IReadOnlyList<ReadOnlyMemory<byte>> responses = await HandlePacketAsync(handler, packet);

        // Assert
        var responsePacket = Assert.Single(responses).ToArray();
        Assert.True(Ipv4Packet.TryParse(responsePacket, out var responseIpv4));
        Assert.True(UdpDatagram.TryParse(responseIpv4, out var datagram));
        Assert.Equal([0x12, 0x34], hijacker.Query.ToArray());
        Assert.Equal([0xAB, 0xCD], datagram.Payload.ToArray());
        Assert.Equal(53, datagram.SourcePort);
        Assert.Equal(53000, datagram.DestinationPort);
    }

    [Fact]
    public async Task CreateHandler_WithRoutedFakeIp_WritesPayloadToSelectedOutbound()
    {
        // Arrange
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var fakeIp = store.Allocate("api.anthropic.com");
        var router = new RecordingRouter(RouteDecision.Through("premium"));
        var defaultOutbound = new RecordingOutbound { Name = "default" };
        var premiumOutbound = new RecordingOutbound { Name = "premium" };
        await using RawTcpTunPipeline pipeline = RawTcpTunPipeline.Create(
            fakeIpStore: store,
            router: router,
            outbounds: new Dictionary<string, IOutbound>(StringComparer.OrdinalIgnoreCase)
            {
                [defaultOutbound.Name] = defaultOutbound,
                [premiumOutbound.Name] = premiumOutbound,
            },
            serverInitialSequence: 9_000);
        ITunPacketHandler handler = pipeline.PacketHandler;

        await HandlePacketAsync(handler, PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: fakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_000,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn));

        await HandlePacketAsync(handler, PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: fakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Ack));

        byte[] payload = [0x42];

        // Act
        IReadOnlyList<ReadOnlyMemory<byte>> responses = await HandlePacketAsync(handler, PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: fakeIp.GetAddressBytes(),
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Psh | TcpFlags.Ack,
            payload: payload));

        // Assert
        Assert.Equal(new ConnectionContext("api.anthropic.com", 443), router.Context);
        Assert.Empty(defaultOutbound.Stream.WrittenBytes);
        Assert.Equal(payload, premiumOutbound.Stream.WrittenBytes.ToArray());
        Assert.Single(responses);
    }

    private static async Task<IReadOnlyList<ReadOnlyMemory<byte>>> HandlePacketAsync(ITunPacketHandler handler, byte[] packet)
        => await handler.HandleAsync(packet, TestContext.Current.CancellationToken);

    private sealed class RecordingOutbound : IOutbound
    {
        public string Name { get; init; } = "test";

        public RecordingStream Stream { get; } = new();

        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<Stream>(Stream);
    }

    private sealed class RecordingRouter(RouteDecision decision) : IRouter
    {
        public ConnectionContext? Context { get; private set; }

        public ValueTask<RouteDecision> RouteAsync(ConnectionContext context, CancellationToken cancellationToken = default)
        {
            Context = context;
            return ValueTask.FromResult(decision);
        }
    }

    private sealed class RecordingStream : MemoryStream
    {
        public byte[] WrittenBytes => ToArray();
    }

    private sealed class StubDnsHijacker(DnsHandlingResult result) : IDnsHijacker
    {
        public ReadOnlyMemory<byte> Query { get; private set; }

        public ValueTask<DnsHandlingResult> HandleAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken = default)
        {
            Query = query;
            return ValueTask.FromResult(result);
        }
    }
}
