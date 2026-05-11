using DotnetTun.Abstractions;
using DotnetTun.Abstractions.Dns;
using DotnetTun.Abstractions.Routing;
using DotnetTun.Core.Dns;

namespace DotnetTun.Core.Sessions;

public sealed class OutboundTcpPayloadSink : ITcpPayloadSink, IAsyncDisposable
{
    private readonly FakeIpPool? _fakeIpPool;
    private readonly IFakeIpStore? _fakeIpStore;
    private readonly IOutbound? _outbound;
    private readonly IRouter? _router;
    private readonly IReadOnlyDictionary<string, IOutbound>? _outbounds;
    private readonly TimeSpan? _responseReadTimeout;
    private readonly int _responseBufferSize;
    private readonly int _maxActiveSessions;
    private readonly Func<TcpSession, ReadOnlyMemory<byte>, CancellationToken, ValueTask>? _remotePayloadHandler;
    private readonly Dictionary<TcpFlowKey, OutboundSession> _sessions = [];

    public OutboundTcpPayloadSink(
        FakeIpPool fakeIpPool,
        IOutbound outbound,
        TimeSpan? responseReadTimeout = null,
        int responseBufferSize = 16 * 1024,
        int maxActiveSessions = 1024,
        Func<TcpSession, ReadOnlyMemory<byte>, CancellationToken, ValueTask>? remotePayloadHandler = null)
    {
        _fakeIpPool = fakeIpPool ?? throw new ArgumentNullException(nameof(fakeIpPool));
        _outbound = outbound ?? throw new ArgumentNullException(nameof(outbound));
        _responseReadTimeout = responseReadTimeout;
        _responseBufferSize = responseBufferSize;
        _maxActiveSessions = ValidateMaxActiveSessions(maxActiveSessions);
        _remotePayloadHandler = remotePayloadHandler;
    }

    public OutboundTcpPayloadSink(
        IFakeIpStore fakeIpStore,
        IRouter router,
        IReadOnlyDictionary<string, IOutbound> outbounds,
        TimeSpan? responseReadTimeout = null,
        int responseBufferSize = 16 * 1024,
        int maxActiveSessions = 1024,
        Func<TcpSession, ReadOnlyMemory<byte>, CancellationToken, ValueTask>? remotePayloadHandler = null)
    {
        _fakeIpStore = fakeIpStore ?? throw new ArgumentNullException(nameof(fakeIpStore));
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _outbounds = ValidateOutbounds(outbounds);
        _responseReadTimeout = responseReadTimeout;
        _responseBufferSize = responseBufferSize;
        _maxActiveSessions = ValidateMaxActiveSessions(maxActiveSessions);
        _remotePayloadHandler = remotePayloadHandler;
    }

    public async ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> WriteAsync(TcpSession session, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
    {
        var outboundSession = await GetOrConnectAsync(session, cancellationToken).ConfigureAwait(false);
        await outboundSession.Stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await outboundSession.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);

        return _remotePayloadHandler is null
            ? await ReadResponsePayloadsAsync(outboundSession.Stream, cancellationToken).ConfigureAwait(false)
            : [];
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await CloseSessionAsync(session).ConfigureAwait(false);
        }

        _sessions.Clear();
    }

    public async ValueTask CloseAsync(TcpSession session, CancellationToken cancellationToken = default)
    {
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

        var domain = ResolveDomain(session);
        var outbound = await ResolveOutboundAsync(domain, session.Key.DestinationPort, cancellationToken).ConfigureAwait(false);
        var stream = await outbound.ConnectAsync(domain, session.Key.DestinationPort, cancellationToken).ConfigureAwait(false);
        var outboundSession = CreateOutboundSession(session, stream);
        _sessions.Add(session.Key, outboundSession);
        return outboundSession;
    }

    private string ResolveDomain(TcpSession session)
    {
        if (_fakeIpStore is not null && _fakeIpStore.TryResolve(session.Key.DestinationAddress, out string? routedDomain))
        {
            return routedDomain;
        }

        if (_fakeIpPool is not null && _fakeIpPool.TryResolve(session.Key.DestinationAddress, out string? legacyDomain))
        {
            return legacyDomain;
        }

        throw new InvalidOperationException($"No domain lease exists for fake IP {session.Key.DestinationAddress}.");
    }

    private async ValueTask<IOutbound> ResolveOutboundAsync(string domain, int port, CancellationToken cancellationToken)
    {
        if (_router is null)
        {
            return _outbound!;
        }

        var decision = await _router.RouteAsync(new ConnectionContext(domain, port), cancellationToken).ConfigureAwait(false);
        if (!decision.Intercept)
        {
            throw new InvalidOperationException($"Direct TCP routing decisions are not supported for {domain}:{port}.");
        }

        var outboundName = decision.OutboundName
            ?? throw new InvalidOperationException($"Route decision for {domain}:{port} did not specify an outbound name.");

        return _outbounds!.TryGetValue(outboundName, out IOutbound? outbound)
            ? outbound
            : throw new InvalidOperationException($"No outbound named '{outboundName}' is registered.");
    }

    private OutboundSession CreateOutboundSession(TcpSession session, Stream stream)
    {
        if (_remotePayloadHandler is null || _responseBufferSize <= 0)
        {
            return new OutboundSession(stream, null, null);
        }

        var readCancellation = new CancellationTokenSource();
        var readTask = ReadRemotePayloadsAsync(session, stream, readCancellation.Token);
        return new OutboundSession(stream, readCancellation, readTask);
    }

    private async ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> ReadResponsePayloadsAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (_responseReadTimeout is null || _responseBufferSize <= 0)
        {
            return [];
        }

        var buffer = new byte[_responseBufferSize];
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_responseReadTimeout.Value);

        try
        {
            var bytesRead = await stream.ReadAsync(buffer, timeoutSource.Token).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                return [];
            }

            var payload = buffer.AsSpan(0, bytesRead).ToArray();
            return [payload];
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        {
            return [];
        }
    }

    private async Task ReadRemotePayloadsAsync(TcpSession session, Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new byte[_responseBufferSize];
        while (!cancellationToken.IsCancellationRequested)
        {
            var bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (bytesRead <= 0)
            {
                return;
            }

            var payload = buffer.AsSpan(0, bytesRead).ToArray();
            await _remotePayloadHandler!(session, payload, cancellationToken).ConfigureAwait(false);
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

    private static IReadOnlyDictionary<string, IOutbound> ValidateOutbounds(IReadOnlyDictionary<string, IOutbound> outbounds)
    {
        ArgumentNullException.ThrowIfNull(outbounds);

        return outbounds.Count > 0
            ? outbounds
            : throw new ArgumentException("At least one outbound must be registered.", nameof(outbounds));
    }
}
