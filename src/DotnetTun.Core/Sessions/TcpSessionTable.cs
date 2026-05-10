using System.Collections.Concurrent;

namespace DotnetTun.Core.Sessions;

public sealed class TcpSessionTable
{
    private readonly ConcurrentDictionary<TcpFlowKey, TcpSession> _sessions = new();
    private readonly object _capacityLock = new();
    private readonly int _maxSessions;

    public TcpSessionTable(int maxSessions = 1024)
    {
        if (maxSessions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSessions), "TCP session table capacity must be at least 1.");
        }

        _maxSessions = maxSessions;
    }

    public int Count => _sessions.Count;

    public TcpSession GetOrAddSynReceived(TcpFlowKey key, uint clientInitialSequence, uint serverInitialSequence)
    {
        if (TryGetOrAddSynReceived(key, clientInitialSequence, serverInitialSequence, out var session) && session is not null)
        {
            return session;
        }

        throw new InvalidOperationException("TCP session table capacity has been reached.");
    }

    public bool TryGetOrAddSynReceived(TcpFlowKey key, uint clientInitialSequence, uint serverInitialSequence, out TcpSession? session)
    {
        if (_sessions.TryGetValue(key, out session))
        {
            return true;
        }

        lock (_capacityLock)
        {
            if (_sessions.TryGetValue(key, out session))
            {
                return true;
            }

            if (_sessions.Count >= _maxSessions)
            {
                session = null;
                return false;
            }

            session = new TcpSession(
                key,
                clientInitialSequence,
                serverInitialSequence,
                clientInitialSequence + 1,
                serverInitialSequence + 1,
                TcpSessionState.SynReceived);

            return _sessions.TryAdd(key, session);
        }
    }

    public bool TryGet(TcpFlowKey key, out TcpSession? session)
        => _sessions.TryGetValue(key, out session);

    public bool TryRemove(TcpFlowKey key, out TcpSession? session)
        => _sessions.TryRemove(key, out session);

    public bool TryEstablish(TcpFlowKey key, uint acknowledgmentNumber)
    {
        while (_sessions.TryGetValue(key, out TcpSession? session))
        {
            if (session.State != TcpSessionState.SynReceived || acknowledgmentNumber != session.NextServerSequence)
            {
                return false;
            }

            TcpSession establishedSession = session with { State = TcpSessionState.Established };
            if (_sessions.TryUpdate(key, establishedSession, session))
            {
                return true;
            }
        }

        return false;
    }

    public bool TryAdvanceClientSequence(TcpFlowKey key, uint nextClientSequence, out TcpSession? updatedSession)
    {
        while (_sessions.TryGetValue(key, out TcpSession? session))
        {
            if (session.State != TcpSessionState.Established)
            {
                updatedSession = null;
                return false;
            }

            TcpSession advancedSession = session with { NextClientSequence = nextClientSequence };
            if (_sessions.TryUpdate(key, advancedSession, session))
            {
                updatedSession = advancedSession;
                return true;
            }
        }

        updatedSession = null;
        return false;
    }

    public bool TryAdvanceServerSequence(TcpFlowKey key, uint nextServerSequence, out TcpSession? updatedSession)
    {
        while (_sessions.TryGetValue(key, out TcpSession? session))
        {
            if (session.State != TcpSessionState.Established)
            {
                updatedSession = null;
                return false;
            }

            TcpSession advancedSession = session with { NextServerSequence = nextServerSequence };
            if (_sessions.TryUpdate(key, advancedSession, session))
            {
                updatedSession = advancedSession;
                return true;
            }
        }

        updatedSession = null;
        return false;
    }
}
