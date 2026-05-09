using DotnetTun.Abstractions.Diagnostics;
using DotnetTun.Core.Routing;

namespace DotnetTun.Core.Dns;

public sealed class FakeDnsResolver(DomainInterceptRouter router, IProxyLogger? logger = null)
{
    private readonly IProxyLogger _logger = logger ?? NullProxyLogger.Instance;

    public bool TryResolve(ReadOnlySpan<byte> query, out byte[]? response)
    {
        response = null;
        if (!FakeDnsMessage.TryReadQuestion(query, out DnsQuestion question) || question.RecordType != DnsRecordType.A)
        {
            _logger.DnsInvalidQuery(query.Length);
            return false;
        }

        var decision = router.Decide(question.Domain);
        if (!decision.Intercepted || decision.FakeIp is null)
        {
            _logger.DnsBypassed(question.Domain);
            return false;
        }

        response = FakeDnsMessage.CreateAResponse(question, decision.FakeIp);
        _logger.DnsIntercepted(question.Domain, decision.FakeIp);
        return true;
    }
}
