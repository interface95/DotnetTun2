using System.Net;
using DotnetTun.Abstractions.Routing;
using DotnetTun.Core.Dns;
using DotnetTun.Core.Routing;
using Xunit;

namespace DotnetTun.Core.Tests.Dns;

public sealed class FakeDnsResolverTests
{
    [Fact]
    public void TryResolve_WithInterceptedAQuery_ReturnsFakeIpResponse()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var router = new DomainInterceptRouter([new DomainInterceptRule("api.anthropic.com")], pool);
        var resolver = new FakeDnsResolver(router);
        byte[] query = DnsTestPackets.CreateAQuery(0xCAFE, "api.anthropic.com");

        // Act
        bool resolved = resolver.TryResolve(query, out byte[]? response);

        // Assert
        Assert.True(resolved);
        Assert.NotNull(response);
        Assert.Equal([198, 18, 0, 1], response[^4..]);
    }

    [Fact]
    public void TryResolve_WithDirectDomain_ReturnsFalse()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var router = new DomainInterceptRouter([new DomainInterceptRule("api.anthropic.com")], pool);
        var resolver = new FakeDnsResolver(router);
        byte[] query = DnsTestPackets.CreateAQuery(0xCAFE, "github.com");

        // Act
        bool resolved = resolver.TryResolve(query, out byte[]? response);

        // Assert
        Assert.False(resolved);
        Assert.Null(response);
    }
}
