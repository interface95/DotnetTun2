namespace DotnetTun.Abstractions;

public sealed record TunOptions
{
    public string Address { get; init; } = "10.88.0.2/24";

    public int Mtu { get; init; } = 1420;
}
