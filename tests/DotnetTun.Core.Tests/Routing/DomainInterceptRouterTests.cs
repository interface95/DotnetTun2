using System.Net;
using DotnetTun.Abstractions.Routing;
using DotnetTun.Core.Dns;
using DotnetTun.Core.Routing;
using Xunit;

namespace DotnetTun.Core.Tests.Routing;

public sealed class DomainInterceptRouterTests
{
    [Fact]
    public void Decide_WithExactDomain_ReturnsInterceptDecision()
    {
        // Arrange
        var router = CreateRouter("api.anthropic.com");

        // Act
        InterceptDecision decision = router.Decide("api.anthropic.com");

        // Assert
        Assert.True(decision.Intercepted);
        Assert.Equal("api.anthropic.com", decision.Domain);
        Assert.Equal(IPAddress.Parse("198.18.0.1"), decision.FakeIp);
    }

    [Fact]
    public void Decide_WithWildcardDomain_ReturnsInterceptDecision()
    {
        // Arrange
        var router = CreateRouter("*.anthropic.com");

        // Act
        InterceptDecision decision = router.Decide("console.anthropic.com");

        // Assert
        Assert.True(decision.Intercepted);
        Assert.Equal("console.anthropic.com", decision.Domain);
        Assert.Equal(IPAddress.Parse("198.18.0.1"), decision.FakeIp);
    }

    [Fact]
    public void Decide_WithNonMatchingDomain_ReturnsDirectDecision()
    {
        // Arrange
        var router = CreateRouter("*.anthropic.com");

        // Act
        InterceptDecision decision = router.Decide("github.com");

        // Assert
        Assert.False(decision.Intercepted);
        Assert.Equal("github.com", decision.Domain);
        Assert.Null(decision.FakeIp);
    }

    [Fact]
    public void Decide_WithMixedCaseDomain_MatchesCaseInsensitively()
    {
        // Arrange
        var router = CreateRouter("API.Anthropic.com");

        // Act
        InterceptDecision decision = router.Decide("api.anthropic.com");

        // Assert
        Assert.True(decision.Intercepted);
    }

    private static DomainInterceptRouter CreateRouter(params string[] patterns)
    {
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        DomainInterceptRule[] rules = patterns.Select(pattern => new DomainInterceptRule(pattern)).ToArray();
        return new DomainInterceptRouter(rules, pool);
    }
}
