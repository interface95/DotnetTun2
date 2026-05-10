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

    [Fact]
    public async Task ResolveAsync_WithDirectDomainAndUpstream_ReturnsUpstreamResponse()
    {
        // Arrange
        byte[] upstreamResponse = [0x12, 0x34, 0x81, 0x80];
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var router = new DomainInterceptRouter([new DomainInterceptRule("api.anthropic.com")], pool);
        var upstream = new StubUpstreamDnsResolver(upstreamResponse);
        var resolver = new FakeDnsResolver(router, upstream);
        byte[] query = DnsTestPackets.CreateAQuery(0xCAFE, "github.com");

        // Act
        byte[]? response = await resolver.ResolveAsync(query, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(upstreamResponse, response);
        Assert.NotNull(upstream.Query);
        Assert.Equal(query, upstream.Query.Value.ToArray());
    }

    [Fact]
    public async Task ResolveAsync_WithInterceptedAaaaQuery_ReturnsNoDataResponseWithoutUpstream()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var router = new DomainInterceptRouter([new DomainInterceptRule("api.anthropic.com")], pool);
        var upstream = new StubUpstreamDnsResolver([0x00]);
        var resolver = new FakeDnsResolver(router, upstream);
        byte[] query = DnsTestPackets.CreateAaaaQuery(0xCAFE, "api.anthropic.com");

        // Act
        byte[]? response = await resolver.ResolveAsync(query, TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(response);
        Assert.Equal(0x81, response[2]);
        Assert.Equal(0x80, response[3]);
        Assert.Equal(0x00, response[6]);
        Assert.Equal(0x00, response[7]);
        Assert.Null(upstream.Query);
    }

    private sealed class StubUpstreamDnsResolver(byte[] response) : IUpstreamDnsResolver
    {
        public ReadOnlyMemory<byte>? Query { get; private set; }

        public ValueTask<byte[]?> ResolveAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken = default)
        {
            Query = query.ToArray();
            return ValueTask.FromResult<byte[]?>(response);
        }
    }
}
