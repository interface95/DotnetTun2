namespace DotnetTun.Abstractions.Routing;

public sealed record RouteDecision
{
    public required bool Intercept { get; init; }

    public string? OutboundName { get; init; }

    public static RouteDecision Direct() => new() { Intercept = false };

    public static RouteDecision Through(string outboundName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outboundName);
        return new RouteDecision { Intercept = true, OutboundName = outboundName.Trim() };
    }
}
