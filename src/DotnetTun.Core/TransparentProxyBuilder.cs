using DotnetTun.Abstractions;

namespace DotnetTun.Core;

public sealed class TransparentProxyBuilder
{
    private string _tunAddress = "10.88.0.2/24";
    private int _mtu = 1420;
    private string _fakeIpRange = "198.18.0.0/15";
    private readonly List<ProxyRuleOptions> _rules = [];
    private readonly Dictionary<string, IOutbound> _outbounds = new(StringComparer.OrdinalIgnoreCase);

    public TransparentProxyBuilder UseTun(Action<TunBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new TunBuilder(_tunAddress, _mtu);
        configure(builder);
        _tunAddress = builder.Address;
        _mtu = builder.Mtu;
        return this;
    }

    public TransparentProxyBuilder UseDns(Action<DnsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new DnsBuilder(_fakeIpRange);
        configure(builder);
        _fakeIpRange = builder.FakeIpRangeValue;
        return this;
    }

    public TransparentProxyBuilder AddRule(string pattern, string outboundName)
    {
        _rules.Add(new ProxyRuleOptions(NormalizeRequired(pattern, nameof(pattern)), NormalizeRequired(outboundName, nameof(outboundName))));
        return this;
    }

    public TransparentProxyBuilder AddOutbound(string name, IOutbound outbound)
    {
        ArgumentNullException.ThrowIfNull(outbound);
        _outbounds[NormalizeRequired(name, nameof(name))] = outbound;
        return this;
    }

    public ITransparentProxy Build()
    {
        var options = new DotnetTunOptions
        {
            Tun = new TunOptions { Address = _tunAddress, Mtu = _mtu },
            Dns = new DnsOptions { FakeIpRange = _fakeIpRange },
            FakeIpCidr = _fakeIpRange,
            InterceptDomains = [.. _rules.Select(rule => rule.Pattern)],
            Rules = [.. _rules]
        };

        return new BuiltTransparentProxy(options, new Dictionary<string, IOutbound>(_outbounds, StringComparer.OrdinalIgnoreCase));
    }

    private static string NormalizeRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value must not be empty.", parameterName);
        }

        return value.Trim();
    }
}

public sealed class TunBuilder(string address, int mtu)
{
    internal string Address { get; private set; } = address;

    internal int Mtu { get; private set; } = mtu;

    public TunBuilder WithAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new ArgumentException("TUN address must not be empty.", nameof(address));
        }

        Address = address.Trim();
        return this;
    }

    public TunBuilder WithMtu(int mtu)
    {
        if (mtu <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mtu), "MTU must be positive.");
        }

        Mtu = mtu;
        return this;
    }
}

public sealed class DnsBuilder(string fakeIpRange)
{
    internal string FakeIpRangeValue { get; private set; } = fakeIpRange;

    public DnsBuilder FakeIpRange(string fakeIpRange)
    {
        if (string.IsNullOrWhiteSpace(fakeIpRange))
        {
            throw new ArgumentException("Fake-IP range must not be empty.", nameof(fakeIpRange));
        }

        FakeIpRangeValue = fakeIpRange.Trim();
        return this;
    }
}

internal sealed class BuiltTransparentProxy(DotnetTunOptions options, IReadOnlyDictionary<string, IOutbound> outbounds) : ITransparentProxy
{
    public DotnetTunOptions Options { get; } = options;

    public IReadOnlyDictionary<string, IOutbound> Outbounds { get; } = outbounds;

    public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
}
