using DotnetTun.Core.Packets;
using DotnetTun.Core.Tests.Packets;
using DotnetTun.Core.Sessions;
using Xunit;

namespace DotnetTun.Core.Tests.Sessions;

public sealed class RawTcpSessionHandlerTests
{
    [Fact]
    public async Task HandleAsync_WithSynToFakeIp_ReturnsSynAckAndCreatesSession()
    {
        // Arrange
        byte[] synPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_000,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn);
        Assert.True(Ipv4Packet.TryParse(synPacket, out Ipv4Packet requestIp));
        Assert.True(TcpSegment.TryParse(requestIp, out TcpSegment requestTcp));

        var sessions = new TcpSessionTable();
        var handler = new RawTcpSessionHandler(sessions, serverInitialSequence: 9_000);

        // Act
        IReadOnlyList<ReadOnlyMemory<byte>> responses = await handler.HandleAsync(requestIp, requestTcp, TestContext.Current.CancellationToken);

        // Assert
        ReadOnlyMemory<byte> response = Assert.Single(responses);
        Assert.True(Ipv4Packet.TryParse(response, out Ipv4Packet responseIp));
        Assert.True(TcpSegment.TryParse(responseIp, out TcpSegment responseTcp));

        Assert.Equal("198.18.0.1", responseIp.SourceAddress.ToString());
        Assert.Equal("10.0.0.2", responseIp.DestinationAddress.ToString());
        Assert.Equal(443, responseTcp.SourcePort);
        Assert.Equal(54321, responseTcp.DestinationPort);
        Assert.Equal(TcpFlags.Syn | TcpFlags.Ack, responseTcp.Flags);
        Assert.Equal(9_000u, responseTcp.SequenceNumber);
        Assert.Equal(1_001u, responseTcp.AcknowledgmentNumber);
        Assert.True(Ipv4Checksum.IsValid(responseIp.RawPacket.Span));
        Assert.True(TcpChecksum.IsValid(responseIp, responseTcp));

        var key = new TcpFlowKey(requestIp.SourceAddress, requestTcp.SourcePort, requestIp.DestinationAddress, requestTcp.DestinationPort);
        Assert.True(sessions.TryGet(key, out TcpSession? session));
        Assert.NotNull(session);
        Assert.Equal(TcpSessionState.SynReceived, session.Value.State);
        Assert.Equal(1_000u, session.Value.ClientInitialSequence);
        Assert.Equal(9_000u, session.Value.ServerInitialSequence);
        Assert.Equal(1_001u, session.Value.NextClientSequence);
        Assert.Equal(9_001u, session.Value.NextServerSequence);
    }

    [Fact]
    public async Task HandleAsync_WithDuplicateSyn_ReturnsStableSynAckWithoutCreatingSecondSession()
    {
        // Arrange
        byte[] synPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_000,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn);
        Assert.True(Ipv4Packet.TryParse(synPacket, out Ipv4Packet requestIp));
        Assert.True(TcpSegment.TryParse(requestIp, out TcpSegment requestTcp));

        var sessions = new TcpSessionTable();
        var handler = new RawTcpSessionHandler(sessions, serverInitialSequence: 9_000);

        // Act
        IReadOnlyList<ReadOnlyMemory<byte>> firstResponses = await handler.HandleAsync(requestIp, requestTcp, TestContext.Current.CancellationToken);
        IReadOnlyList<ReadOnlyMemory<byte>> secondResponses = await handler.HandleAsync(requestIp, requestTcp, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(1, sessions.Count);
        Assert.True(Ipv4Packet.TryParse(Assert.Single(firstResponses), out Ipv4Packet firstResponseIp));
        Assert.True(TcpSegment.TryParse(firstResponseIp, out TcpSegment firstResponseTcp));
        Assert.True(Ipv4Packet.TryParse(Assert.Single(secondResponses), out Ipv4Packet secondResponseIp));
        Assert.True(TcpSegment.TryParse(secondResponseIp, out TcpSegment secondResponseTcp));
        Assert.Equal(firstResponseTcp.SequenceNumber, secondResponseTcp.SequenceNumber);
        Assert.Equal(firstResponseTcp.AcknowledgmentNumber, secondResponseTcp.AcknowledgmentNumber);
    }

    [Fact]
    public void TcpSessionTable_WhenFull_RejectsNewSynReceivedSession()
    {
        // Arrange
        var sessions = new TcpSessionTable(maxSessions: 1);

        // Act
        bool firstAdded = sessions.TryGetOrAddSynReceived(Flow(sourcePort: 54321), 1_000, 9_000, out var firstSession);
        bool secondAdded = sessions.TryGetOrAddSynReceived(Flow(sourcePort: 54322), 2_000, 9_000, out var secondSession);

        // Assert
        Assert.True(firstAdded);
        Assert.NotNull(firstSession);
        Assert.False(secondAdded);
        Assert.Null(secondSession);
        Assert.Equal(1, sessions.Count);
    }

    [Fact]
    public async Task HandleAsync_WhenTcpSessionTableIsFull_ReturnsNoResponseAndDoesNotGrowState()
    {
        // Arrange
        var sessions = new TcpSessionTable(maxSessions: 1);
        var handler = new RawTcpSessionHandler(sessions, serverInitialSequence: 9_000);

        var firstSyn = CreateSynPacket(sourcePort: 54321, sequenceNumber: 1_000);
        var secondSyn = CreateSynPacket(sourcePort: 54322, sequenceNumber: 2_000);

        await handler.HandleAsync(firstSyn.Ip, firstSyn.Tcp, TestContext.Current.CancellationToken);

        // Act
        var responses = await handler.HandleAsync(secondSyn.Ip, secondSyn.Tcp, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(responses);
        Assert.Equal(1, sessions.Count);
    }

    [Fact]
    public async Task HandleAsync_WithHandshakeAck_EstablishesSessionWithoutResponse()
    {
        // Arrange
        byte[] synPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_000,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn);
        Assert.True(Ipv4Packet.TryParse(synPacket, out Ipv4Packet synIp));
        Assert.True(TcpSegment.TryParse(synIp, out TcpSegment synTcp));

        var sessions = new TcpSessionTable();
        var handler = new RawTcpSessionHandler(sessions, serverInitialSequence: 9_000);
        await handler.HandleAsync(synIp, synTcp, TestContext.Current.CancellationToken);

        byte[] ackPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Ack);
        Assert.True(Ipv4Packet.TryParse(ackPacket, out Ipv4Packet ackIp));
        Assert.True(TcpSegment.TryParse(ackIp, out TcpSegment ackTcp));

        // Act
        IReadOnlyList<ReadOnlyMemory<byte>> responses = await handler.HandleAsync(ackIp, ackTcp, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(responses);
        var key = new TcpFlowKey(ackIp.SourceAddress, ackTcp.SourcePort, ackIp.DestinationAddress, ackTcp.DestinationPort);
        Assert.True(sessions.TryGet(key, out TcpSession? session));
        Assert.NotNull(session);
        Assert.Equal(TcpSessionState.Established, session.Value.State);
    }

    [Fact]
    public async Task HandleAsync_WithInvalidHandshakeAck_DoesNotEstablishSession()
    {
        // Arrange
        byte[] synPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_000,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn);
        Assert.True(Ipv4Packet.TryParse(synPacket, out Ipv4Packet synIp));
        Assert.True(TcpSegment.TryParse(synIp, out TcpSegment synTcp));

        var sessions = new TcpSessionTable();
        var handler = new RawTcpSessionHandler(sessions, serverInitialSequence: 9_000);
        await handler.HandleAsync(synIp, synTcp, TestContext.Current.CancellationToken);

        byte[] ackPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_999,
            flags: TcpFlags.Ack);
        Assert.True(Ipv4Packet.TryParse(ackPacket, out Ipv4Packet ackIp));
        Assert.True(TcpSegment.TryParse(ackIp, out TcpSegment ackTcp));

        // Act
        IReadOnlyList<ReadOnlyMemory<byte>> responses = await handler.HandleAsync(ackIp, ackTcp, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(responses);
        var key = new TcpFlowKey(ackIp.SourceAddress, ackTcp.SourcePort, ackIp.DestinationAddress, ackTcp.DestinationPort);
        Assert.True(sessions.TryGet(key, out TcpSession? session));
        Assert.NotNull(session);
        Assert.Equal(TcpSessionState.SynReceived, session.Value.State);
    }

    [Fact]
    public async Task HandleAsync_WithEstablishedPayload_ForwardsPayloadAndReturnsAck()
    {
        // Arrange
        byte[] synPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_000,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn);
        Assert.True(Ipv4Packet.TryParse(synPacket, out Ipv4Packet synIp));
        Assert.True(TcpSegment.TryParse(synIp, out TcpSegment synTcp));

        var sessions = new TcpSessionTable();
        var payloadSink = new RecordingTcpPayloadSink();
        var handler = new RawTcpSessionHandler(sessions, serverInitialSequence: 9_000, payloadSink);
        await handler.HandleAsync(synIp, synTcp, TestContext.Current.CancellationToken);

        byte[] handshakeAckPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Ack);
        Assert.True(Ipv4Packet.TryParse(handshakeAckPacket, out Ipv4Packet handshakeAckIp));
        Assert.True(TcpSegment.TryParse(handshakeAckIp, out TcpSegment handshakeAckTcp));
        await handler.HandleAsync(handshakeAckIp, handshakeAckTcp, TestContext.Current.CancellationToken);

        byte[] payload = [0x42, 0x43];
        byte[] payloadPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Psh | TcpFlags.Ack,
            payload: payload);
        Assert.True(Ipv4Packet.TryParse(payloadPacket, out Ipv4Packet payloadIp));
        Assert.True(TcpSegment.TryParse(payloadIp, out TcpSegment payloadTcp));

        // Act
        IReadOnlyList<ReadOnlyMemory<byte>> responses = await handler.HandleAsync(payloadIp, payloadTcp, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(payload, payloadSink.Payloads.Single());
        ReadOnlyMemory<byte> response = Assert.Single(responses);
        Assert.True(Ipv4Packet.TryParse(response, out Ipv4Packet responseIp));
        Assert.True(TcpSegment.TryParse(responseIp, out TcpSegment responseTcp));
        Assert.Equal(TcpFlags.Ack, responseTcp.Flags);
        Assert.Equal(9_001u, responseTcp.SequenceNumber);
        Assert.Equal(1_003u, responseTcp.AcknowledgmentNumber);
        Assert.True(TcpChecksum.IsValid(responseIp, responseTcp));

        var key = new TcpFlowKey(payloadIp.SourceAddress, payloadTcp.SourcePort, payloadIp.DestinationAddress, payloadTcp.DestinationPort);
        Assert.True(sessions.TryGet(key, out TcpSession? session));
        Assert.NotNull(session);
        Assert.Equal(1_003u, session.Value.NextClientSequence);
    }

    [Fact]
    public void HandleAsync_WithEstablishedPayload_StaysWithinAllocationBudget()
    {
        var sessions = new TcpSessionTable();
        var handler = new RawTcpSessionHandler(sessions, serverInitialSequence: 9_000, new NoopTcpPayloadSink());
        EstablishSessionSynchronously(handler);

        byte[] payloadPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Psh | TcpFlags.Ack,
            payload: [0x42, 0x43]);
        Assert.True(Ipv4Packet.TryParse(payloadPacket, out var payloadIp));
        Assert.True(TcpSegment.TryParse(payloadIp, out var payloadTcp));

        var before = GC.GetAllocatedBytesForCurrentThread();

        var responses = HandleSynchronously(handler, payloadIp, payloadTcp);

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Single(responses);
        Assert.True(allocatedBytes <= 1_056, $"Allocated {allocatedBytes} bytes.");
    }

    [Fact]
    public async Task HandleAsync_WithDuplicateEstablishedPayload_DoesNotForwardPayloadOrReturnAck()
    {
        // Arrange
        var sessions = new TcpSessionTable();
        var payloadSink = new RecordingTcpPayloadSink();
        var handler = new RawTcpSessionHandler(sessions, serverInitialSequence: 9_000, payloadSink);
        await EstablishSessionAsync(handler);

        byte[] payload = [0x42, 0x43];
        byte[] payloadPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Psh | TcpFlags.Ack,
            payload: payload);
        Assert.True(Ipv4Packet.TryParse(payloadPacket, out var payloadIp));
        Assert.True(TcpSegment.TryParse(payloadIp, out var payloadTcp));
        var firstResponses = await handler.HandleAsync(payloadIp, payloadTcp, TestContext.Current.CancellationToken);
        Assert.Single(firstResponses);

        // Act
        var duplicateResponses = await handler.HandleAsync(payloadIp, payloadTcp, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(duplicateResponses);
        Assert.Equal(payload, Assert.Single(payloadSink.Payloads));

        var key = new TcpFlowKey(payloadIp.SourceAddress, payloadTcp.SourcePort, payloadIp.DestinationAddress, payloadTcp.DestinationPort);
        Assert.True(sessions.TryGet(key, out var session));
        Assert.NotNull(session);
        Assert.Equal(1_003u, session.Value.NextClientSequence);
    }

    [Fact]
    public async Task HandleAsync_WithPayloadSequenceGap_DoesNotForwardPayloadOrReturnAck()
    {
        // Arrange
        var sessions = new TcpSessionTable();
        var payloadSink = new RecordingTcpPayloadSink();
        var handler = new RawTcpSessionHandler(sessions, serverInitialSequence: 9_000, payloadSink);
        await EstablishSessionAsync(handler);

        byte[] payload = [0x42, 0x43];
        byte[] payloadPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_003,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Psh | TcpFlags.Ack,
            payload: payload);
        Assert.True(Ipv4Packet.TryParse(payloadPacket, out var payloadIp));
        Assert.True(TcpSegment.TryParse(payloadIp, out var payloadTcp));

        // Act
        var responses = await handler.HandleAsync(payloadIp, payloadTcp, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(responses);
        Assert.Empty(payloadSink.Payloads);

        var key = new TcpFlowKey(payloadIp.SourceAddress, payloadTcp.SourcePort, payloadIp.DestinationAddress, payloadTcp.DestinationPort);
        Assert.True(sessions.TryGet(key, out var session));
        Assert.NotNull(session);
        Assert.Equal(1_001u, session.Value.NextClientSequence);
    }

    [Fact]
    public async Task HandleAsync_WithOutOfOrderPayload_DoesNotForwardPayloadOrReturnAck()
    {
        // Arrange
        var sessions = new TcpSessionTable();
        var payloadSink = new RecordingTcpPayloadSink();
        var handler = new RawTcpSessionHandler(sessions, serverInitialSequence: 9_000, payloadSink);
        await EstablishSessionAsync(handler);

        byte[] firstPayload = [0x42, 0x43];
        byte[] secondPayload = [0x44, 0x45];
        byte[] outOfOrderPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_003,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Psh | TcpFlags.Ack,
            payload: secondPayload);
        Assert.True(Ipv4Packet.TryParse(outOfOrderPacket, out var outOfOrderIp));
        Assert.True(TcpSegment.TryParse(outOfOrderIp, out var outOfOrderTcp));

        // Act
        var outOfOrderResponses = await handler.HandleAsync(outOfOrderIp, outOfOrderTcp, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(outOfOrderResponses);
        Assert.Empty(payloadSink.Payloads);

        byte[] firstPayloadPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Psh | TcpFlags.Ack,
            payload: firstPayload);
        Assert.True(Ipv4Packet.TryParse(firstPayloadPacket, out var firstPayloadIp));
        Assert.True(TcpSegment.TryParse(firstPayloadIp, out var firstPayloadTcp));
        var firstPayloadResponses = await handler.HandleAsync(firstPayloadIp, firstPayloadTcp, TestContext.Current.CancellationToken);

        Assert.Single(firstPayloadResponses);
        Assert.Equal(firstPayload, Assert.Single(payloadSink.Payloads));
    }

    [Fact]
    public async Task HandleAsync_WithRstForExistingSession_RemovesSessionAndClosesPayloadSink()
    {
        // Arrange
        byte[] synPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_000,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn);
        Assert.True(Ipv4Packet.TryParse(synPacket, out Ipv4Packet synIp));
        Assert.True(TcpSegment.TryParse(synIp, out TcpSegment synTcp));

        var sessions = new TcpSessionTable();
        var payloadSink = new RecordingTcpPayloadSink();
        var handler = new RawTcpSessionHandler(sessions, serverInitialSequence: 9_000, payloadSink);
        await handler.HandleAsync(synIp, synTcp, TestContext.Current.CancellationToken);

        byte[] rstPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Rst);
        Assert.True(Ipv4Packet.TryParse(rstPacket, out Ipv4Packet rstIp));
        Assert.True(TcpSegment.TryParse(rstIp, out TcpSegment rstTcp));

        // Act
        IReadOnlyList<ReadOnlyMemory<byte>> responses = await handler.HandleAsync(rstIp, rstTcp, TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(responses);
        Assert.Equal(0, sessions.Count);
        Assert.NotNull(payloadSink.ClosedSession);
        Assert.Equal(54321, payloadSink.ClosedSession.Value.Key.SourcePort);
    }

    [Fact]
    public async Task HandleAsync_WithFinForEstablishedSession_ReturnsAckAndRemovesSession()
    {
        // Arrange
        byte[] synPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_000,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn);
        Assert.True(Ipv4Packet.TryParse(synPacket, out Ipv4Packet synIp));
        Assert.True(TcpSegment.TryParse(synIp, out TcpSegment synTcp));

        var sessions = new TcpSessionTable();
        var payloadSink = new RecordingTcpPayloadSink();
        var handler = new RawTcpSessionHandler(sessions, serverInitialSequence: 9_000, payloadSink);
        await handler.HandleAsync(synIp, synTcp, TestContext.Current.CancellationToken);

        byte[] handshakeAckPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Ack);
        Assert.True(Ipv4Packet.TryParse(handshakeAckPacket, out Ipv4Packet handshakeAckIp));
        Assert.True(TcpSegment.TryParse(handshakeAckIp, out TcpSegment handshakeAckTcp));
        await handler.HandleAsync(handshakeAckIp, handshakeAckTcp, TestContext.Current.CancellationToken);

        byte[] finPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Fin | TcpFlags.Ack);
        Assert.True(Ipv4Packet.TryParse(finPacket, out Ipv4Packet finIp));
        Assert.True(TcpSegment.TryParse(finIp, out TcpSegment finTcp));

        // Act
        IReadOnlyList<ReadOnlyMemory<byte>> responses = await handler.HandleAsync(finIp, finTcp, TestContext.Current.CancellationToken);

        // Assert
        ReadOnlyMemory<byte> response = Assert.Single(responses);
        Assert.True(Ipv4Packet.TryParse(response, out Ipv4Packet responseIp));
        Assert.True(TcpSegment.TryParse(responseIp, out TcpSegment responseTcp));
        Assert.Equal(TcpFlags.Ack, responseTcp.Flags);
        Assert.Equal(9_001u, responseTcp.SequenceNumber);
        Assert.Equal(1_002u, responseTcp.AcknowledgmentNumber);
        Assert.True(TcpChecksum.IsValid(responseIp, responseTcp));
        Assert.Equal(0, sessions.Count);
        Assert.NotNull(payloadSink.ClosedSession);
    }

    private static async ValueTask EstablishSessionAsync(RawTcpSessionHandler handler)
    {
        byte[] synPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_000,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn);
        Assert.True(Ipv4Packet.TryParse(synPacket, out var synIp));
        Assert.True(TcpSegment.TryParse(synIp, out var synTcp));
        await handler.HandleAsync(synIp, synTcp, TestContext.Current.CancellationToken);

        byte[] handshakeAckPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Ack);
        Assert.True(Ipv4Packet.TryParse(handshakeAckPacket, out var handshakeAckIp));
        Assert.True(TcpSegment.TryParse(handshakeAckIp, out var handshakeAckTcp));
        await handler.HandleAsync(handshakeAckIp, handshakeAckTcp, TestContext.Current.CancellationToken);
    }

    private static void EstablishSessionSynchronously(RawTcpSessionHandler handler)
        => EstablishSessionAsync(handler).GetAwaiter().GetResult();

    private static IReadOnlyList<ReadOnlyMemory<byte>> HandleSynchronously(RawTcpSessionHandler handler, Ipv4Packet packet, TcpSegment segment)
        => handler.HandleAsync(packet, segment, TestContext.Current.CancellationToken).GetAwaiter().GetResult();

    private static TcpFlowKey Flow(int sourcePort)
        => new(
            System.Net.IPAddress.Parse("10.0.0.2"),
            sourcePort,
            System.Net.IPAddress.Parse("198.18.0.1"),
            443);

    private static (Ipv4Packet Ip, TcpSegment Tcp) CreateSynPacket(int sourcePort, uint sequenceNumber)
    {
        byte[] synPacket = PacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: sourcePort,
            destinationPort: 443,
            sequenceNumber: sequenceNumber,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn);
        Assert.True(Ipv4Packet.TryParse(synPacket, out var synIp));
        Assert.True(TcpSegment.TryParse(synIp, out var synTcp));
        return (synIp, synTcp);
    }

    private sealed class RecordingTcpPayloadSink : ITcpPayloadSink
    {
        public List<byte[]> Payloads { get; } = [];

        public ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> WriteAsync(TcpSession session, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            Payloads.Add(payload.ToArray());
            IReadOnlyList<ReadOnlyMemory<byte>> responses = [];
            return ValueTask.FromResult(responses);
        }

        public ValueTask CloseAsync(TcpSession session, CancellationToken cancellationToken = default)
        {
            ClosedSession = session;
            return ValueTask.CompletedTask;
        }

        public TcpSession? ClosedSession { get; private set; }
    }

    private sealed class NoopTcpPayloadSink : ITcpPayloadSink
    {
        public ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> WriteAsync(TcpSession session, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<byte>>>([]);

        public ValueTask CloseAsync(TcpSession session, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
