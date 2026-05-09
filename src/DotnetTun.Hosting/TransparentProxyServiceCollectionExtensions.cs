using DotnetTun.Abstractions;
using DotnetTun.Core;
using Microsoft.Extensions.DependencyInjection;

namespace DotnetTun.Hosting;

public static class TransparentProxyServiceCollectionExtensions
{
    public static TransparentProxyServiceBuilder AddTransparentProxy(this IServiceCollection services, Action<TransparentProxyOptionsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var optionsBuilder = new TransparentProxyOptionsBuilder();
        configure(optionsBuilder);

        var builder = new TransparentProxyServiceBuilder(services, optionsBuilder.TunAddress, optionsBuilder.TunMtu, optionsBuilder.FakeIpRange);
        services.AddSingleton<ITransparentProxy>(_ => builder.BuildProxy());
        return builder;
    }
}

public sealed class TransparentProxyServiceBuilder
{
    private readonly IServiceCollection _services;
    private readonly TransparentProxyBuilder _proxyBuilder = TransparentProxy.CreateBuilder();

    internal TransparentProxyServiceBuilder(IServiceCollection services, string tunAddress, int tunMtu, string fakeIpRange)
    {
        _services = services;
        _proxyBuilder
            .UseTun(t => t.WithAddress(tunAddress).WithMtu(tunMtu))
            .UseDns(d => d.FakeIpRange(fakeIpRange));
    }

    public TransparentProxyServiceBuilder AddDomainRule(string pattern, string outboundName)
    {
        _proxyBuilder.AddRule(pattern, outboundName);
        return this;
    }

    public TransparentProxyServiceBuilder AddOutbound<TOutbound>(string name, Action<TOutbound> configure)
        where TOutbound : class, IOutbound, new()
    {
        ArgumentNullException.ThrowIfNull(configure);

        var outbound = new TOutbound();
        configure(outbound);
        _proxyBuilder.AddOutbound(name, outbound);
        _services.AddSingleton(outbound);
        return this;
    }

    internal ITransparentProxy BuildProxy() => _proxyBuilder.Build();
}

public sealed class TransparentProxyOptionsBuilder
{
    public MutableTunOptions Tun { get; } = new();

    public MutableDnsOptions Dns { get; } = new();

    internal string TunAddress => Tun.Address;

    internal int TunMtu => Tun.Mtu;

    internal string FakeIpRange => Dns.FakeIpRange;
}

public sealed class MutableTunOptions
{
    public string Address { get; set; } = "10.88.0.2/24";

    public int Mtu { get; set; } = 1420;
}

public sealed class MutableDnsOptions
{
    public string FakeIpRange { get; set; } = "198.18.0.0/15";
}
