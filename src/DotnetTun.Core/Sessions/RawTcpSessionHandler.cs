using DotnetTun.Core.Packets;

namespace DotnetTun.Core.Sessions;

public sealed class RawTcpSessionHandler(TcpSessionTable sessions, uint serverInitialSequence, ITcpPayloadSink? payloadSink = null) : ITcpSegmentHandler
{
    private readonly TcpSessionTable _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public async ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(Ipv4Packet packet, TcpSegment segment, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TcpFlowKey key = new(packet.SourceAddress, segment.SourcePort, packet.DestinationAddress, segment.DestinationPort);

        if (segment.IsRst)
        {
            if (_sessions.TryRemove(key, out TcpSession? removedSession) && removedSession is not null && payloadSink is not null)
            {
                await payloadSink.CloseAsync(removedSession, cancellationToken).ConfigureAwait(false);
            }

            return [];
        }

        if (segment.IsFin)
        {
            if (_sessions.TryRemove(key, out TcpSession? removedSession) && removedSession is not null)
            {
                if (payloadSink is not null)
                {
                    await payloadSink.CloseAsync(removedSession, cancellationToken).ConfigureAwait(false);
                }

                byte[] response = TcpPacketBuilder.Build(
                    packet.DestinationAddress,
                    packet.SourceAddress,
                    segment.DestinationPort,
                    segment.SourcePort,
                    removedSession.NextServerSequence,
                    segment.SequenceNumber + 1,
                    TcpFlags.Ack);

                IReadOnlyList<ReadOnlyMemory<byte>> responses = [response];
                return responses;
            }

            return [];
        }

        if (segment.IsSyn && !segment.IsAck)
        {
            if (!_sessions.TryGetOrAddSynReceived(key, segment.SequenceNumber, serverInitialSequence, out var session)
                || session is null)
            {
                return [];
            }

            byte[] response = TcpPacketBuilder.Build(
                packet.DestinationAddress,
                packet.SourceAddress,
                segment.DestinationPort,
                segment.SourcePort,
                session.ServerInitialSequence,
                session.NextClientSequence,
            TcpFlags.Syn | TcpFlags.Ack);

            IReadOnlyList<ReadOnlyMemory<byte>> responses = [response];
            return responses;
        }

        if (segment.IsAck && segment.Payload.IsEmpty)
        {
            _ = _sessions.TryEstablish(key, segment.AcknowledgmentNumber);
        }

        if (segment.IsAck && !segment.Payload.IsEmpty)
        {
            if (!_sessions.TryGet(key, out var currentSession)
                || currentSession is null
                || currentSession.State != TcpSessionState.Established
                || segment.SequenceNumber != currentSession.NextClientSequence)
            {
                return [];
            }

            uint nextClientSequence = segment.SequenceNumber + (uint)segment.Payload.Length;
            if (_sessions.TryAdvanceClientSequence(key, nextClientSequence, out TcpSession? session) && session is not null)
            {
                IReadOnlyList<ReadOnlyMemory<byte>> remotePayloads = payloadSink is null
                    ? []
                    : await payloadSink.WriteAsync(session, segment.Payload, cancellationToken).ConfigureAwait(false);

                byte[] ackResponse = TcpPacketBuilder.Build(
                    packet.DestinationAddress,
                    packet.SourceAddress,
                    segment.DestinationPort,
                    segment.SourcePort,
                    session.NextServerSequence,
                    nextClientSequence,
                    TcpFlags.Ack);

                var responses = new List<ReadOnlyMemory<byte>> { ackResponse };
                TcpSession serverSession = session;
                foreach (ReadOnlyMemory<byte> remotePayload in remotePayloads)
                {
                    byte[] serverPayloadPacket = TcpPacketBuilder.Build(
                        packet.DestinationAddress,
                        packet.SourceAddress,
                        segment.DestinationPort,
                        segment.SourcePort,
                        serverSession.NextServerSequence,
                        nextClientSequence,
                        TcpFlags.Psh | TcpFlags.Ack,
                        remotePayload.Span);
                    responses.Add(serverPayloadPacket);

                    uint nextServerSequence = serverSession.NextServerSequence + (uint)remotePayload.Length;
                    if (_sessions.TryAdvanceServerSequence(key, nextServerSequence, out TcpSession? updatedSession) && updatedSession is not null)
                    {
                        serverSession = updatedSession;
                    }
                }

                return responses;
            }
        }

        IReadOnlyList<ReadOnlyMemory<byte>> empty = [];
        return empty;
    }
}
