using DotnetTun.Abstractions.Dns;
using DotnetTun.Core.Packets;
using DotnetTun.Core.Sessions;
using DotnetTun.Core.Tests.Packets;
using Xunit;

namespace DotnetTun.Core.Tests.Sessions;

public sealed class Dns53SinkTests
{
    [Fact]
    public async Task HandleAsync_WithDnsResponse_ReturnsUdpReplyWithSwappedTuple()
    {
        var packet = PacketFixtures.CreateUdpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            sourcePort: 53000,
            destinationPort: 53,
            payload: [0x12, 0x34]);
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));
        Assert.True(UdpDatagram.TryParse(ipv4Packet, out var datagram));
        var hijacker = new StubDnsHijacker(DnsHandlingResult.Intercepted([0xAB, 0xCD]));
        var sink = new Dns53Sink(hijacker);

        var responses = await sink.HandleAsync(ipv4Packet, datagram, TestContext.Current.CancellationToken);

        var responsePacket = Assert.Single(responses).ToArray();
        Assert.True(Ipv4Packet.TryParse(responsePacket, out var responseIpv4));
        Assert.True(UdpDatagram.TryParse(responseIpv4, out var responseDatagram));
        Assert.True(UdpChecksum.IsValid(responseIpv4, responseDatagram));
        Assert.Equal("198.18.0.1", responseIpv4.SourceAddress.ToString());
        Assert.Equal("10.0.0.1", responseIpv4.DestinationAddress.ToString());
        Assert.Equal(53, responseDatagram.SourcePort);
        Assert.Equal(53000, responseDatagram.DestinationPort);
        Assert.Equal([0xAB, 0xCD], responseDatagram.Payload.ToArray());
        Assert.Equal([0x12, 0x34], hijacker.Query.ToArray());
    }

    [Fact]
    public async Task HandleAsync_WithNonDnsDestinationPort_DropsWithoutCallingHijacker()
    {
        var packet = PacketFixtures.CreateUdpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            sourcePort: 53000,
            destinationPort: 5353,
            payload: [0x12, 0x34]);
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));
        Assert.True(UdpDatagram.TryParse(ipv4Packet, out var datagram));
        var hijacker = new StubDnsHijacker(DnsHandlingResult.Intercepted([0xAB, 0xCD]));
        var sink = new Dns53Sink(hijacker);

        var responses = await sink.HandleAsync(ipv4Packet, datagram, TestContext.Current.CancellationToken);

        Assert.Empty(responses);
        Assert.False(hijacker.WasCalled);
    }

    [Fact]
    public async Task HandleAsync_WhenHijackerDrops_ReturnsNoResponses()
    {
        var packet = PacketFixtures.CreateUdpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            sourcePort: 53000,
            destinationPort: 53,
            payload: [0x12, 0x34]);
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));
        Assert.True(UdpDatagram.TryParse(ipv4Packet, out var datagram));
        var sink = new Dns53Sink(new StubDnsHijacker(DnsHandlingResult.Dropped()));

        var responses = await sink.HandleAsync(ipv4Packet, datagram, TestContext.Current.CancellationToken);

        Assert.Empty(responses);
    }

    private sealed class StubDnsHijacker(DnsHandlingResult result) : IDnsHijacker
    {
        public bool WasCalled { get; private set; }

        public ReadOnlyMemory<byte> Query { get; private set; }

        public ValueTask<DnsHandlingResult> HandleAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            Query = query;
            return ValueTask.FromResult(result);
        }
    }
}
