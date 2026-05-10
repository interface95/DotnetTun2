using DotnetTun.Core.Packets;
using DotnetTun.Core.Sessions;
using DotnetTun.Core.Tests.Packets;
using Xunit;

namespace DotnetTun.Core.Tests.Sessions;

public sealed class TcpIpv4PacketHandlerTests
{
    [Fact]
    public async Task HandleAsync_WithTcpIpv4Packet_DelegatesParsedSegment()
    {
        // Arrange
        var packet = PacketFixtures.CreateTcpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            54321,
            443,
            1,
            0,
            TcpFlags.Syn);
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));
        byte[] response = [0x45, 0x00];
        var handler = new RecordingTcpSegmentHandler(response);
        var adapter = new TcpIpv4PacketHandler(handler);

        // Act
        var responses = await adapter.HandleAsync(ipv4Packet, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(54321, handler.Segment.SourcePort);
        Assert.Equal(443, handler.Segment.DestinationPort);
        Assert.Equal(TcpFlags.Syn, handler.Segment.Flags);
        Assert.Equal("198.18.0.1", handler.Packet.DestinationAddress.ToString());
        Assert.Collection(responses, item => Assert.Equal(response, item.ToArray()));
    }

    [Fact]
    public async Task HandleAsync_WithInvalidTcpChecksum_ReturnsNoResponsesAndDoesNotDelegate()
    {
        // Arrange
        var packet = PacketFixtures.CreateTcpPacket(
            [0x0A, 0x00, 0x00, 0x01],
            [0xC6, 0x12, 0x00, 0x01],
            54321,
            443,
            1,
            0,
            TcpFlags.Syn);
        packet[36] ^= 0xFF;
        Assert.True(Ipv4Packet.TryParse(packet, out var ipv4Packet));
        byte[] response = [0x45, 0x00];
        var handler = new RecordingTcpSegmentHandler(response);
        var adapter = new TcpIpv4PacketHandler(handler);

        // Act
        var responses = await adapter.HandleAsync(ipv4Packet, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(responses);
        Assert.Equal(0, handler.HandleCallCount);
    }

    private sealed class RecordingTcpSegmentHandler(byte[] response) : ITcpSegmentHandler
    {
        public int HandleCallCount { get; private set; }

        public Ipv4Packet Packet { get; private set; }

        public TcpSegment Segment { get; private set; }

        public ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(Ipv4Packet packet, TcpSegment segment, CancellationToken cancellationToken = default)
        {
            HandleCallCount++;
            Packet = packet;
            Segment = segment;
            IReadOnlyList<ReadOnlyMemory<byte>> responses = [response];
            return ValueTask.FromResult(responses);
        }
    }
}
