using System.Net;
using DotnetTun.Abstractions.Dns;
using DotnetTun.Abstractions.Routing;
using DotnetTun.Core.Dns;
using Xunit;

namespace DotnetTun.Core.Tests.Dns;

public sealed class RoutingDnsHijackerTests
{
    [Fact]
    public async Task HandleAsync_WhenRouterInterceptsAQuery_ReturnsAResponseFromStore()
    {
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.10"));
        IRouter router = new StubRouter(("api.anthropic.com", RouteDecision.Through("h2")));
        var hijacker = new RoutingDnsHijacker(router, store, upstream: null);

        var result = await hijacker.HandleAsync(DnsQueryBuilder.BuildAQuery("api.anthropic.com", id: 0x1234), TestContext.Current.CancellationToken);

        Assert.Equal(DnsHandlingDisposition.Intercepted, result.Disposition);
        Assert.NotNull(result.Response);
        Assert.True(store.TryResolve(IPAddress.Parse("198.18.0.1"), out var domain));
        Assert.Equal("api.anthropic.com", domain);
    }

    [Fact]
    public async Task HandleAsync_WhenRouterInterceptsCachedAQuery_StaysWithinAllocationBudget()
    {
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.10"));
        IRouter router = new StubRouter(("api.anthropic.com", RouteDecision.Through("h2")));
        var hijacker = new RoutingDnsHijacker(router, store, upstream: null);
        var query = DnsQueryBuilder.BuildAQuery("api.anthropic.com", id: 0x1234);
        var warmup = await hijacker.HandleAsync(query, TestContext.Current.CancellationToken);
        Assert.Equal(DnsHandlingDisposition.Intercepted, warmup.Disposition);

        var before = GC.GetAllocatedBytesForCurrentThread();

        var result = await hijacker.HandleAsync(query, TestContext.Current.CancellationToken);

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(DnsHandlingDisposition.Intercepted, result.Disposition);
        Assert.NotNull(result.Response);
        Assert.True(allocatedBytes <= 512, $"Allocated {allocatedBytes} bytes.");
    }

    [Fact]
    public async Task HandleAsync_WhenRouterInterceptsAaaaQuery_ReturnsNoDataResponse()
    {
        IFakeIpStore store = new FakeIpStore(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.10"));
        IRouter router = new StubRouter(("api.anthropic.com", RouteDecision.Through("h2")));
        var hijacker = new RoutingDnsHijacker(router, store, upstream: null);

        var result = await hijacker.HandleAsync(DnsQueryBuilder.BuildAaaaQuery("api.anthropic.com", id: 0x1234), TestContext.Current.CancellationToken);

        Assert.Equal(DnsHandlingDisposition.Intercepted, result.Disposition);
        Assert.NotNull(result.Response);
        Assert.False(store.TryResolve(IPAddress.Parse("198.18.0.1"), out _));
    }

    [Fact]
    public async Task HandleAsync_WhenRouterDirectAndUpstreamPresent_ReturnsForwardedResponse()
    {
        var upstream = new StubUpstreamResolver([0xAA, 0xBB]);
        var hijacker = new RoutingDnsHijacker(new StubRouter(), new FakeIpStore(), upstream);

        var result = await hijacker.HandleAsync(DnsQueryBuilder.BuildAQuery("example.com", id: 0x1234), TestContext.Current.CancellationToken);

        Assert.Equal(DnsHandlingDisposition.Forwarded, result.Disposition);
        Assert.Equal([0xAA, 0xBB], result.Response);
    }

    [Fact]
    public async Task HandleAsync_WhenRouterDirectAndNoUpstream_ReturnsDropped()
    {
        var hijacker = new RoutingDnsHijacker(new StubRouter(), new FakeIpStore(), upstream: null);

        var result = await hijacker.HandleAsync(DnsQueryBuilder.BuildAQuery("example.com", id: 0x1234), TestContext.Current.CancellationToken);

        Assert.Equal(DnsHandlingDisposition.Dropped, result.Disposition);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task HandleAsync_WhenQueryIsMalformed_ReturnsDropped()
    {
        var hijacker = new RoutingDnsHijacker(new StubRouter(), new FakeIpStore(), upstream: null);

        var result = await hijacker.HandleAsync(new byte[] { 0x00, 0x01 }, TestContext.Current.CancellationToken);

        Assert.Equal(DnsHandlingDisposition.Dropped, result.Disposition);
        Assert.Null(result.Response);
    }

    private sealed class StubRouter(params (string Host, RouteDecision Decision)[] entries) : IRouter
    {
        public ValueTask<RouteDecision> RouteAsync(ConnectionContext context, CancellationToken cancellationToken = default)
        {
            foreach (var (host, decision) in entries)
            {
                if (string.Equals(host, context.Host, StringComparison.OrdinalIgnoreCase))
                {
                    return ValueTask.FromResult(decision);
                }
            }

            return ValueTask.FromResult(RouteDecision.Direct());
        }
    }

    private sealed class StubUpstreamResolver(byte[] response) : IUpstreamDnsResolver
    {
        public ValueTask<byte[]?> ResolveAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<byte[]?>(response);
    }
}
