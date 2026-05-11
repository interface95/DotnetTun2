using DotnetTun.Abstractions.Routing;
using DotnetTun.Core.Routing;
using Xunit;

namespace DotnetTun.Core.Tests.Routing;

public sealed class DomainRuleRouterTests
{
    [Fact]
    public async Task RouteAsync_WhenExactDomainMatches_ReturnsThroughDecision()
    {
        var router = new DomainRuleRouter([new DomainInterceptRule("api.anthropic.com", "h2")]);

        var decision = await router.RouteAsync(new ConnectionContext("api.anthropic.com", 443), TestContext.Current.CancellationToken);

        Assert.True(decision.Intercept);
        Assert.Equal("h2", decision.OutboundName);
    }

    [Fact]
    public async Task RouteAsync_WhenSuffixMatches_ReturnsThroughDecision()
    {
        var router = new DomainRuleRouter([new DomainInterceptRule("*.anthropic.com", "h2")]);

        var decision = await router.RouteAsync(new ConnectionContext("api.anthropic.com", 443), TestContext.Current.CancellationToken);

        Assert.True(decision.Intercept);
        Assert.Equal("h2", decision.OutboundName);
    }

    [Fact]
    public async Task RouteAsync_WhenWildcardRootDomainMatches_ReturnsThroughDecision()
    {
        var router = new DomainRuleRouter([new DomainInterceptRule("*.anthropic.com", "h2")]);

        var decision = await router.RouteAsync(new ConnectionContext("anthropic.com", 443), TestContext.Current.CancellationToken);

        Assert.True(decision.Intercept);
        Assert.Equal("h2", decision.OutboundName);
    }

    [Fact]
    public async Task RouteAsync_WhenNoRuleMatches_ReturnsDirect()
    {
        var router = new DomainRuleRouter([new DomainInterceptRule("api.anthropic.com", "h2")]);

        var decision = await router.RouteAsync(new ConnectionContext("example.com", 443), TestContext.Current.CancellationToken);

        Assert.False(decision.Intercept);
        Assert.Null(decision.OutboundName);
    }

    [Fact]
    public async Task RouteAsync_DomainComparisonIsCaseInsensitive()
    {
        var router = new DomainRuleRouter([new DomainInterceptRule("API.anthropic.com", "h2")]);

        var decision = await router.RouteAsync(new ConnectionContext("api.ANTHROPIC.com", 443), TestContext.Current.CancellationToken);

        Assert.True(decision.Intercept);
        Assert.Equal("h2", decision.OutboundName);
    }
}
