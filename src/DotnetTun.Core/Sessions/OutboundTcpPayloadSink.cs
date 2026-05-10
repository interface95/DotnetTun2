using DotnetTun.Abstractions;
using DotnetTun.Core.Dns;

namespace DotnetTun.Core.Sessions;

public sealed class OutboundTcpPayloadSink(
    FakeIpPool fakeIpPool,
    IOutbound outbound,
    TimeSpan? responseReadTimeout = null,
    int responseBufferSize = 16 * 1024,
    int maxActiveSessions = 1024,
    Func<TcpSession, ReadOnlyMemory<byte>, CancellationToken, ValueTask>? remotePayloadHandler = null) : ITcpPayloadSink, IAsyncDisposable
{
    private readonly FakeIpPool _fakeIpPool = fakeIpPool ?? throw new ArgumentNullException(nameof(fakeIpPool));
    private readonly IOutbound _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
    private readonly int _maxActiveSessions = ValidateMaxActiveSessions(maxActiveSessions);
    private readonly Dictionary<TcpFlowKey, OutboundSession> _sessions = [];

    public async ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> WriteAsync(TcpSession session, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        OutboundSession outboundSession = await GetOrConnectAsync(session, cancellationToken).ConfigureAwait(false);
        await outboundSession.Stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await outboundSession.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        return remotePayloadHandler is null
            ? await ReadResponsePayloadsAsync(outboundSession.Stream, cancellationToken).ConfigureAwait(false)
            : [];
    }

    public async ValueTask DisposeAsync()
    {
        foreach (OutboundSession session in _sessions.Values)
        {
            await CloseSessionAsync(session).ConfigureAwait(false);
        }

        _sessions.Clear();
    }

    public async ValueTask CloseAsync(TcpSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        if (_sessions.Remove(session.Key, out OutboundSession? outboundSession))
        {
            await CloseSessionAsync(outboundSession).ConfigureAwait(false);
        }
    }

    private async ValueTask<OutboundSession> GetOrConnectAsync(TcpSession session, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(session.Key, out OutboundSession? existing))
        {
            return existing;
        }

        if (_sessions.Count >= _maxActiveSessions)
        {
            throw new InvalidOperationException($"The active outbound TCP session limit of {_maxActiveSessions} has been reached.");
        }

        if (!_fakeIpPool.TryResolve(session.Key.DestinationAddress, out string? domain))
        {
            throw new InvalidOperationException($"No domain lease exists for fake IP {session.Key.DestinationAddress}.");
        }

        Stream stream = await _outbound.ConnectAsync(domain, session.Key.DestinationPort, cancellationToken).ConfigureAwait(false);
        OutboundSession outboundSession = CreateOutboundSession(session, stream);
        _sessions.Add(session.Key, outboundSession);
        return outboundSession;
    }

    private OutboundSession CreateOutboundSession(TcpSession session, Stream stream)
    {
        if (remotePayloadHandler is null || responseBufferSize <= 0)
        {
            return new OutboundSession(stream, null, null);
        }

        var readCancellation = new CancellationTokenSource();
        Task readTask = ReadRemotePayloadsAsync(session, stream, readCancellation.Token);
        return new OutboundSession(stream, readCancellation, readTask);
    }

    private async ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> ReadResponsePayloadsAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (responseReadTimeout is null || responseBufferSize <= 0)
        {
            return [];
        }

        byte[] buffer = new byte[responseBufferSize];
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(responseReadTimeout.Value);

        try
        {
            int bytesRead = await stream.ReadAsync(buffer, timeoutSource.Token).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                return [];
            }

            byte[] payload = buffer.AsSpan(0, bytesRead).ToArray();
            return [payload];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            return [];
        }
    }

    private async Task ReadRemotePayloadsAsync(TcpSession session, Stream stream, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[responseBufferSize];
        while (!cancellationToken.IsCancellationRequested)
        {
            int bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                return;
            }

            byte[] payload = buffer.AsSpan(0, bytesRead).ToArray();
            await remotePayloadHandler!(session, payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask CloseSessionAsync(OutboundSession session)
    {
        if (session.ReadCancellation is not null)
        {
            await session.ReadCancellation.CancelAsync().ConfigureAwait(false);
            if (session.ReadTask is not null)
            {
                try
                {
                    await session.ReadTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (IsExpectedCancellation(exception, session.ReadCancellation.Token))
                {
                    IgnoreExpectedCancellation(exception);
                }
            }

            session.ReadCancellation.Dispose();
        }

        await session.Stream.DisposeAsync().ConfigureAwait(false);
    }

    private sealed record OutboundSession(Stream Stream, CancellationTokenSource? ReadCancellation, Task? ReadTask);

    private static bool IsExpectedCancellation(OperationCanceledException exception, CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested;

    private static void IgnoreExpectedCancellation(OperationCanceledException exception)
        => _ = exception;

    private static int ValidateMaxActiveSessions(int maxActiveSessions)
        => maxActiveSessions > 0
            ? maxActiveSessions
            : throw new ArgumentOutOfRangeException(nameof(maxActiveSessions), maxActiveSessions, "Maximum active sessions must be greater than zero.");
}
