using DotnetTun.Abstractions;
using DotnetTun.Core;
using Xunit;

namespace DotnetTun.Core.Tests.Engine;

public sealed class DotnetTunEngineTests
{
    [Fact]
    public void CreateDryRun_WithEmptyDomain_ThrowsArgumentException()
    {
        // Arrange
        var options = new DotnetTunOptions
        {
            InterceptDomains = ["api.anthropic.com", " "]
        };
        var engine = new DotnetTunEngine();

        // Act
        var act = () => engine.CreateDryRun(options);

        // Assert
        Assert.Throws<ArgumentException>(act);
    }

    [Fact]
    public void CreateDryRun_WithClaudeDomains_ReturnsExactDomainFakeIpLeases()
    {
        // Arrange
        var options = new DotnetTunOptions
        {
            InterceptDomains = ["api.anthropic.com", "*.anthropic.com", "events.statsigapi.net"]
        };
        var engine = new DotnetTunEngine();

        // Act
        DotnetTunDryRunPlan plan = engine.CreateDryRun(options);

        // Assert
        Assert.Collection(
            plan.ExactDomainLeases,
            lease => Assert.Equal("api.anthropic.com", lease.Domain),
            lease => Assert.Equal("events.statsigapi.net", lease.Domain));
        Assert.Equal(["*.anthropic.com"], plan.WildcardPatterns);
    }
}
