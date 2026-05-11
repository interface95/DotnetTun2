using DotnetTun.Abstractions;
using DotnetTun.Core;
using Xunit;

namespace DotnetTun.Core.Tests.Builder;

public sealed class TransparentProxyBuilderTests
{
    [Fact]
    public void Build_WithFluentApi_CreatesTransparentProxyConfiguration()
    {
        // Arrange
        var outbound = new TestOutbound("https://server/");

        // Act
        ITransparentProxy proxy = TransparentProxy.CreateBuilder()
            .UseTun(t => t.WithAddress("10.0.0.1/24").WithMtu(1500))
            .UseDns(d => d.FakeIpRange("198.18.0.0/15"))
            .AddRule("*.example.com", "h2")
            .AddOutbound("h2", outbound)
            .Build();

        // Assert
        Assert.Equal("10.0.0.1/24", proxy.Options.Tun.Address);
        Assert.Equal(1500, proxy.Options.Tun.Mtu);
        Assert.Equal("198.18.0.0/15", proxy.Options.Dns.FakeIpRange);
        Assert.Collection(proxy.Options.Rules, rule =>
        {
            Assert.Equal("*.example.com", rule.Pattern);
            Assert.Equal("h2", rule.OutboundName);
        });
        Assert.Same(outbound, Assert.Single(proxy.Outbounds).Value);
    }

    private sealed record TestOutbound(string Endpoint) : IOutbound
    {
        public string Name => "test";

        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Test outbound is configuration-only.");
    }
}
