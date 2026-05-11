using DotnetTun.Core.Packets;
using DotnetTun.Core.Sessions;
using DotnetTun.Core.Tests.Packets;
using Xunit;

namespace DotnetTun.Core.Tests.Sessions;

public sealed class Ipv4ProtocolDispatcherTests
{
    [Fact]
    public async Task HandleAsync_WithTcpPacket_DelegatesToTcpHandler()
    {
        var packet = PacketFixtures.CreateTcpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            53000,
            443,
            1,
            0,
            TcpFlags.Syn);
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));
        var tcpHandler = new RecordingIpv4PacketHandler([0x45, 0x01]);
        var udpHandler = new RecordingIpv4PacketHandler([0x45, 0x02]);
        var dispatcher = new Ipv4ProtocolDispatcher(tcpHandler, udpHandler);

        var responses = await dispatcher.HandleAsync(ipv4Packet, TestContext.Current.CancellationToken);

        Assert.True(tcpHandler.WasCalled);
        Assert.False(udpHandler.WasCalled);
        Assert.Collection(responses, item => Assert.Equal(new byte[] { 0x45, 0x01 }, item.ToArray()));
    }

    [Fact]
    public async Task HandleAsync_WithUdpPacket_DelegatesToUdpHandler()
    {
        var packet = PacketFixtures.CreateUdpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            53000,
            53,
            [0x12, 0x34]);
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));
        var tcpHandler = new RecordingIpv4PacketHandler([0x45, 0x01]);
        var udpHandler = new RecordingIpv4PacketHandler([0x45, 0x02]);
        var dispatcher = new Ipv4ProtocolDispatcher(tcpHandler, udpHandler);

        var responses = await dispatcher.HandleAsync(ipv4Packet, TestContext.Current.CancellationToken);

        Assert.False(tcpHandler.WasCalled);
        Assert.True(udpHandler.WasCalled);
        Assert.Collection(responses, item => Assert.Equal(new byte[] { 0x45, 0x02 }, item.ToArray()));
    }

    [Fact]
    public async Task HandleAsync_WithUdpPacketAndNoUdpHandler_DropsPacket()
    {
        var packet = PacketFixtures.CreateUdpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            53000,
            53,
            [0x12, 0x34]);
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));
        var tcpHandler = new RecordingIpv4PacketHandler([0x45, 0x01]);
        var dispatcher = new Ipv4ProtocolDispatcher(tcpHandler);

        var responses = await dispatcher.HandleAsync(ipv4Packet, TestContext.Current.CancellationToken);

        Assert.Empty(responses);
        Assert.False(tcpHandler.WasCalled);
    }

    private sealed class RecordingIpv4PacketHandler(byte[] response) : IIpv4PacketHandler
    {
        public bool WasCalled { get; private set; }

        public ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(Ipv4Packet packet, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            IReadOnlyList<ReadOnlyMemory<byte>> responses = [response];
            return ValueTask.FromResult(responses);
        }
    }
}
