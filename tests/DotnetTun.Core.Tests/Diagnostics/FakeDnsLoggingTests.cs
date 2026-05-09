using System.Net;
using DotnetTun.Abstractions.Diagnostics;
using DotnetTun.Abstractions.Routing;
using DotnetTun.Core.Dns;
using DotnetTun.Core.Routing;
using DotnetTun.Core.Tests.Dns;
using Xunit;

namespace DotnetTun.Core.Tests.Diagnostics;

public sealed class FakeDnsLoggingTests
{
    [Fact]
    public void TryResolve_WithInterceptedDomain_LogsDnsIntercepted()
    {
        // Arrange
        var logger = new RecordingProxyLogger();
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var router = new DomainInterceptRouter([new DomainInterceptRule("api.anthropic.com")], pool);
        var resolver = new FakeDnsResolver(router, logger);
        byte[] query = DnsTestPackets.CreateAQuery(0x1000, "api.anthropic.com");

        // Act
        bool resolved = resolver.TryResolve(query, out _);

        // Assert
        Assert.True(resolved);
        Assert.Collection(logger.Events, entry => Assert.Equal("[DNS] intercepted api.anthropic.com -> 198.18.0.1", entry));
    }

    [Fact]
    public void TryResolve_WithDirectDomain_LogsDnsBypassed()
    {
        // Arrange
        var logger = new RecordingProxyLogger();
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var router = new DomainInterceptRouter([new DomainInterceptRule("api.anthropic.com")], pool);
        var resolver = new FakeDnsResolver(router, logger);
        byte[] query = DnsTestPackets.CreateAQuery(0x1000, "github.com");

        // Act
        bool resolved = resolver.TryResolve(query, out _);

        // Assert
        Assert.False(resolved);
        Assert.Collection(logger.Events, entry => Assert.Equal("[DNS] bypass github.com", entry));
    }

    private sealed class RecordingProxyLogger : IProxyLogger
    {
        public List<string> Events { get; } = [];

        public void DnsIntercepted(string domain, IPAddress fakeIp) => Events.Add($"[DNS] intercepted {domain} -> {fakeIp}");

        public void DnsBypassed(string domain) => Events.Add($"[DNS] bypass {domain}");

        public void DnsInvalidQuery(int bytesReceived) => Events.Add($"[DNS] invalid query {bytesReceived} bytes");
    }
}
