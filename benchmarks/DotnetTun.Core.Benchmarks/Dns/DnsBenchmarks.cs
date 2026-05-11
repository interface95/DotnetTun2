using BenchmarkDotNet.Attributes;
using DotnetTun.Abstractions.Dns;
using DotnetTun.Abstractions.Routing;
using DotnetTun.Core.Benchmarks.Support;
using DotnetTun.Core.Dns;

namespace DotnetTun.Core.Benchmarks.Dns;

[MemoryDiagnoser]
[ShortRunJob]
public class DnsBenchmarks
{
    private RoutingDnsHijacker _hijacker = null!;
    private byte[] _query = [];

    [GlobalSetup]
    public void GlobalSetup()
    {
        _query = BenchmarkDnsQueryBuilder.BuildAQuery("api.anthropic.com", id: 0x1234);
        _hijacker = new RoutingDnsHijacker(new InterceptingRouter(), new FakeIpStore());
    }

    [Benchmark]
    public async Task<int> Handle_InterceptedAQuery()
    {
        var result = await _hijacker.HandleAsync(_query).ConfigureAwait(false);
        return result.Disposition == DnsHandlingDisposition.Intercepted
            ? result.Response?.Length ?? 0
            : 0;
    }

    private sealed class InterceptingRouter : IRouter
    {
        public ValueTask<RouteDecision> RouteAsync(ConnectionContext context, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(RouteDecision.Through("benchmark"));
    }
}
