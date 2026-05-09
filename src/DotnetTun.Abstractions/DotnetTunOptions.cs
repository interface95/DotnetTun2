using System.Net;

namespace DotnetTun.Abstractions;

public sealed record DotnetTunOptions
{
    public TunOptions Tun { get; init; } = new();

    public DnsOptions Dns { get; init; } = new();

    public IReadOnlyList<ProxyRuleOptions> Rules { get; init; } = [];

    public IReadOnlyList<string> InterceptDomains { get; init; } = [];

    public string FakeIpCidr { get; init; } = "198.18.0.0/15";

    public IReadOnlyList<IPAddress> ExcludedIps { get; init; } = [];
}
