using DotnetTun.Abstractions.Dns;
using DotnetTun.Abstractions.Routing;

namespace DotnetTun.Core.Dns;

public sealed class RoutingDnsHijacker : IDnsHijacker
{
    private readonly IRouter _router;
    private readonly IFakeIpStore _store;
    private readonly IUpstreamDnsResolver? _upstream;

    public RoutingDnsHijacker(IRouter router, IFakeIpStore store, IUpstreamDnsResolver? upstream = null)
    {
        _router = router ?? throw new ArgumentNullException(nameof(router));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _upstream = upstream;
    }

    public async ValueTask<DnsHandlingResult> HandleAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken = default)
    {
        if (!FakeDnsMessage.TryReadQuestion(query.Span, out var question))
        {
            return DnsHandlingResult.Dropped();
        }

        var decision = await _router.RouteAsync(new ConnectionContext(question.Domain, port: 0), cancellationToken).ConfigureAwait(false);
        if (decision.Intercept)
        {
            if (question.RecordType == DnsRecordType.Aaaa)
            {
                return DnsHandlingResult.Intercepted(FakeDnsMessage.CreateNoDataResponse(question));
            }

            if (question.RecordType != DnsRecordType.A)
            {
                return DnsHandlingResult.Dropped();
            }

            var fakeIp = _store.Allocate(question.Domain);
            return DnsHandlingResult.Intercepted(FakeDnsMessage.CreateAResponse(question, fakeIp));
        }

        if (_upstream is null)
        {
            return DnsHandlingResult.Dropped();
        }

        var upstreamResponse = await _upstream.ResolveAsync(query, cancellationToken).ConfigureAwait(false);
        return upstreamResponse is null
            ? DnsHandlingResult.Dropped()
            : DnsHandlingResult.Forwarded(upstreamResponse);
    }
}
