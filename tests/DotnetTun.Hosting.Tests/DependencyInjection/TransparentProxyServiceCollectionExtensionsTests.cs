using DotnetTun.Abstractions;
using DotnetTun.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DotnetTun.Hosting.Tests.DependencyInjection;

public sealed class TransparentProxyServiceCollectionExtensionsTests
{
    [Fact]
    public void AddTransparentProxy_WithFluentApi_RegistersConfiguredProxy()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        services.AddTransparentProxy(options =>
            {
                options.Tun.Address = "10.0.0.1/24";
                options.Tun.Mtu = 1500;
                options.Dns.FakeIpRange = "198.18.0.0/15";
            })
            .AddDomainRule("*.example.com", "h2")
            .AddOutbound<TestOutbound>("h2", outbound => outbound.Endpoint = "https://server/");

        using ServiceProvider provider = services.BuildServiceProvider();
        ITransparentProxy proxy = provider.GetRequiredService<ITransparentProxy>();

        // Assert
        Assert.Equal("10.0.0.1/24", proxy.Options.Tun.Address);
        Assert.Equal(1500, proxy.Options.Tun.Mtu);
        Assert.Equal("198.18.0.0/15", proxy.Options.Dns.FakeIpRange);
        Assert.Collection(proxy.Options.Rules, rule =>
        {
            Assert.Equal("*.example.com", rule.Pattern);
            Assert.Equal("h2", rule.OutboundName);
        });
        var outbound = Assert.IsType<TestOutbound>(Assert.Single(proxy.Outbounds).Value);
        Assert.Equal("https://server/", outbound.Endpoint);
    }

    private sealed class TestOutbound : IOutbound
    {
        public string Name => "test";

        public string Endpoint { get; set; } = string.Empty;

        public ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Test outbound is configuration-only.");
    }
}
