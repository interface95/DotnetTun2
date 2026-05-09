using System.Net;
using DotnetTun.Core.Dns;
using Xunit;

namespace DotnetTun.Core.Tests.FakeIp;

public sealed class FakeIpPoolTests
{
    [Fact]
    public void Allocate_WithSameDomain_ReturnsSameFakeIp()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));

        // Act
        var first = pool.Allocate("api.anthropic.com");
        var second = pool.Allocate("API.Anthropic.com");

        // Assert
        Assert.Equal(first.FakeIp, second.FakeIp);
        Assert.Equal("api.anthropic.com", first.Domain);
    }

    [Fact]
    public void Allocate_WithDifferentDomains_ReturnsDifferentFakeIps()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));

        // Act
        var anthropic = pool.Allocate("api.anthropic.com");
        var statsig = pool.Allocate("events.statsigapi.net");

        // Assert
        Assert.NotEqual(anthropic.FakeIp, statsig.FakeIp);
    }

    [Fact]
    public void TryResolve_WithAllocatedFakeIp_ReturnsOriginalDomain()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var lease = pool.Allocate("api.anthropic.com");

        // Act
        var resolved = pool.TryResolve(lease.FakeIp, out var domain);

        // Assert
        Assert.True(resolved);
        Assert.Equal("api.anthropic.com", domain);
    }

    [Fact]
    public void Allocate_WhenRangeIsExhausted_ThrowsInvalidOperationException()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.1"));
        pool.Allocate("api.anthropic.com");

        // Act
        var act = () => pool.Allocate("console.anthropic.com");

        // Assert
        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void Constructor_WithIpv6Range_ThrowsArgumentException()
    {
        // Arrange
        var start = IPAddress.Parse("2001:db8::1");
        var end = IPAddress.Parse("2001:db8::2");

        // Act
        var act = () => new FakeIpPool(start, end);

        // Assert
        Assert.Throws<ArgumentException>(act);
    }
}
