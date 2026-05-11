using DotnetTun.Core.Packets;

namespace DotnetTun.Core.Sessions;

public sealed class RawTcpSessionHandler(TcpSessionTable sessions, uint serverInitialSequence, ITcpPayloadSink? payloadSink = null) : ITcpSegmentHandler
{
    private readonly TcpSessionTable _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));

    public async ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(Ipv4Packet packet, TcpSegment segment, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = new TcpFlowKey(packet.SourceAddressBits, segment.SourcePort, packet.DestinationAddressBits, segment.DestinationPort);

        if (segment.IsRst)
        {
            if (_sessions.TryRemove(key, out var removedSession) && removedSession is not null && payloadSink is not null)
            {
                await payloadSink.CloseAsync(removedSession.Value, cancellationToken).ConfigureAwait(false);
            }

            return [];
        }

        if (segment.IsFin)
        {
            if (_sessions.TryRemove(key, out var removedSession) && removedSession is not null)
            {
                if (payloadSink is not null)
                {
                    await payloadSink.CloseAsync(removedSession.Value, cancellationToken).ConfigureAwait(false);
                }

                var response = TcpPacketBuilder.Build(
                    packet.DestinationAddressBits,
                    packet.SourceAddressBits,
                    segment.DestinationPort,
                    segment.SourcePort,
                    removedSession.Value.NextServerSequence,
                    segment.SequenceNumber + 1,
                    TcpFlags.Ack);

                ReadOnlyMemory<byte>[] responses = [response];
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

            var synReceivedSession = session.Value;

            var response = TcpPacketBuilder.Build(
                packet.DestinationAddressBits,
                packet.SourceAddressBits,
                segment.DestinationPort,
                segment.SourcePort,
                synReceivedSession.ServerInitialSequence,
                synReceivedSession.NextClientSequence,
                TcpFlags.Syn | TcpFlags.Ack);

            ReadOnlyMemory<byte>[] responses = [response];
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
                || currentSession.Value.State != TcpSessionState.Established
                || segment.SequenceNumber != currentSession.Value.NextClientSequence)
            {
                return [];
            }

            var nextClientSequence = segment.SequenceNumber + (uint)segment.Payload.Length;
            if (_sessions.TryAdvanceClientSequence(key, nextClientSequence, out TcpSession? session) && session is not null)
            {
                var advancedSession = session.Value;
                var remotePayloads = payloadSink is null
                    ? []
                    : await payloadSink.WriteAsync(advancedSession, segment.Payload, cancellationToken).ConfigureAwait(false);

                var ackResponse = TcpPacketBuilder.Build(
                    packet.DestinationAddressBits,
                    packet.SourceAddressBits,
                    segment.DestinationPort,
                    segment.SourcePort,
                    advancedSession.NextServerSequence,
                    nextClientSequence,
                    TcpFlags.Ack);

                if (remotePayloads.Count == 0)
                {
                    ReadOnlyMemory<byte>[] ackOnlyResponse = [ackResponse];
                    return ackOnlyResponse;
                }

                var responses = new List<ReadOnlyMemory<byte>>(remotePayloads.Count + 1) { ackResponse };
                var serverSession = advancedSession;
                foreach (var remotePayload in remotePayloads)
                {
                    var serverPayloadPacket = TcpPacketBuilder.Build(
                        packet.DestinationAddressBits,
                        packet.SourceAddressBits,
                        segment.DestinationPort,
                        segment.SourcePort,
                        serverSession.NextServerSequence,
                        nextClientSequence,
                        TcpFlags.Psh | TcpFlags.Ack,
                        remotePayload.Span);
                    responses.Add(serverPayloadPacket);

                    var nextServerSequence = serverSession.NextServerSequence + (uint)remotePayload.Length;
                    if (_sessions.TryAdvanceServerSequence(key, nextServerSequence, out TcpSession? updatedSession) && updatedSession is not null)
                    {
                        serverSession = updatedSession.Value;
                    }
                }

                return responses;
            }
        }

        IReadOnlyList<ReadOnlyMemory<byte>> empty = [];
        return empty;
    }
}
