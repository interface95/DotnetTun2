namespace DotnetTun.Core.Sessions;

public sealed class TcpSessionTable
{
    private readonly Dictionary<TcpFlowKey, TcpSession> _sessions = [];
    private readonly object _gate = new();
    private readonly int _maxSessions;

    public TcpSessionTable(int maxSessions = 1024)
    {
        if (maxSessions < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSessions), "TCP session table capacity must be at least 1.");
        }

        _maxSessions = maxSessions;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _sessions.Count;
            }
        }
    }

    public TcpSession GetOrAddSynReceived(TcpFlowKey key, uint clientInitialSequence, uint serverInitialSequence)
    {
        if (TryGetOrAddSynReceived(key, clientInitialSequence, serverInitialSequence, out var session) && session is not null)
        {
            return session.Value;
        }

        throw new InvalidOperationException("TCP session table capacity has been reached.");
    }

    public bool TryGetOrAddSynReceived(TcpFlowKey key, uint clientInitialSequence, uint serverInitialSequence, out TcpSession? session)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(key, out var existingSession))
            {
                session = existingSession;
                return true;
            }

            if (_sessions.Count >= _maxSessions)
            {
                session = null;
                return false;
            }

            var newSession = new TcpSession(
                key,
                clientInitialSequence,
                serverInitialSequence,
                clientInitialSequence + 1,
                serverInitialSequence + 1,
                TcpSessionState.SynReceived);

            _sessions.Add(key, newSession);
            session = newSession;
            return true;
        }
    }

    public bool TryGet(TcpFlowKey key, out TcpSession? session)
    {
        lock (_gate)
        {
            if (_sessions.TryGetValue(key, out var existingSession))
            {
                session = existingSession;
                return true;
            }

            session = null;
            return false;
        }
    }

    public bool TryRemove(TcpFlowKey key, out TcpSession? session)
    {
        lock (_gate)
        {
            if (_sessions.Remove(key, out var removedSession))
            {
                session = removedSession;
                return true;
            }

            session = null;
            return false;
        }
    }

    public bool TryEstablish(TcpFlowKey key, uint acknowledgmentNumber)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var session))
            {
                return false;
            }

            if (session.State != TcpSessionState.SynReceived || acknowledgmentNumber != session.NextServerSequence)
            {
                return false;
            }

            _sessions[key] = session with { State = TcpSessionState.Established };
            return true;
        }
    }

    public bool TryAdvanceClientSequence(TcpFlowKey key, uint nextClientSequence, out TcpSession? updatedSession)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var session))
            {
                updatedSession = null;
                return false;
            }

            if (session.State != TcpSessionState.Established)
            {
                updatedSession = null;
                return false;
            }

            TcpSession advancedSession = session with { NextClientSequence = nextClientSequence };
            _sessions[key] = advancedSession;
            updatedSession = advancedSession;
            return true;
        }
    }

    public bool TryAdvanceServerSequence(TcpFlowKey key, uint nextServerSequence, out TcpSession? updatedSession)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(key, out var session))
            {
                updatedSession = null;
                return false;
            }

            if (session.State != TcpSessionState.Established)
            {
                updatedSession = null;
                return false;
            }

            TcpSession advancedSession = session with { NextServerSequence = nextServerSequence };
            _sessions[key] = advancedSession;
            updatedSession = advancedSession;
            return true;
        }
    }
}
