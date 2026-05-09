namespace DotnetTun.Abstractions;

public sealed record DnsOptions
{
    public string FakeIpRange { get; init; } = "198.18.0.0/15";
}
