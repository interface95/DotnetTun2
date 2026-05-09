using DotnetTun.Core.Routing;

namespace DotnetTun.Core.Dns;

public sealed class FakeDnsResolver(DomainInterceptRouter router)
{
    public bool TryResolve(ReadOnlySpan<byte> query, out byte[]? response)
    {
        response = null;
        if (!FakeDnsMessage.TryReadQuestion(query, out DnsQuestion question) || question.RecordType != DnsRecordType.A)
        {
            return false;
        }

        var decision = router.Decide(question.Domain);
        if (!decision.Intercepted || decision.FakeIp is null)
        {
            return false;
        }

        response = FakeDnsMessage.CreateAResponse(question, decision.FakeIp);
        return true;
    }
}
