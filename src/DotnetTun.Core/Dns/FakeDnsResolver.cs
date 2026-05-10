using DotnetTun.Abstractions.Diagnostics;
using DotnetTun.Core.Routing;

namespace DotnetTun.Core.Dns;

public sealed class FakeDnsResolver
{
    private readonly DomainInterceptRouter _router;
    private readonly IProxyLogger _logger;
    private readonly IUpstreamDnsResolver? _upstreamResolver;

    public FakeDnsResolver(DomainInterceptRouter router, IProxyLogger? logger = null)
        : this(router, logger, upstreamResolver: null)
    {
    }

    public FakeDnsResolver(DomainInterceptRouter router, IUpstreamDnsResolver upstreamResolver)
        : this(router, logger: null, upstreamResolver)
    {
    }

    public FakeDnsResolver(DomainInterceptRouter router, IProxyLogger? logger, IUpstreamDnsResolver? upstreamResolver)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _logger = logger ?? NullProxyLogger.Instance;
        _upstreamResolver = upstreamResolver;
    }

    public bool TryResolve(ReadOnlySpan<byte> query, out byte[]? response)
    {
        return TryResolveCore(query, out response, allowDirectFallback: false);
    }

    public async ValueTask<byte[]?> ResolveAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken = default)
    {
        if (TryResolveCore(query.Span, out byte[]? response, allowDirectFallback: true))
        {
            return response;
        }

        if (!FakeDnsMessage.TryReadQuestion(query.Span, out DnsQuestion question))
        {
            _logger.DnsInvalidQuery(query.Length);
            return null;
        }

        _logger.DnsBypassed(question.Domain);
        return _upstreamResolver is null
            ? null
            : await _upstreamResolver.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
    }

    private bool TryResolveCore(ReadOnlySpan<byte> query, out byte[]? response, bool allowDirectFallback)
    {
        response = null;
        if (!FakeDnsMessage.TryReadQuestion(query, out DnsQuestion question))
        {
            _logger.DnsInvalidQuery(query.Length);
            return false;
        }

        var decision = _router.Decide(question.Domain);
        if (!decision.Intercepted || decision.FakeIp is null)
        {
            if (!allowDirectFallback)
            {
                _logger.DnsBypassed(question.Domain);
            }

            return false;
        }

        if (question.RecordType == DnsRecordType.Aaaa)
        {
            response = FakeDnsMessage.CreateNoDataResponse(question);
            return true;
        }

        if (question.RecordType != DnsRecordType.A)
        {
            _logger.DnsInvalidQuery(query.Length);
            return false;
        }

        response = FakeDnsMessage.CreateAResponse(question, decision.FakeIp);
        _logger.DnsIntercepted(question.Domain, decision.FakeIp);
        return true;
    }
}
