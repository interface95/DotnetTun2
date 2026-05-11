namespace DotnetTun.Abstractions.Routing;

public sealed record DomainInterceptRule
{
    public DomainInterceptRule(string pattern)
        : this(pattern, "default")
    {
    }

    public DomainInterceptRule(string pattern, string outboundName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(outboundName);

        Pattern = pattern.Trim();
        OutboundName = outboundName.Trim();
    }

    public string Pattern { get; }

    public string OutboundName { get; }
}
