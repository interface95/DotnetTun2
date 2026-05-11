using DotnetTun.Abstractions.Routing;

namespace DotnetTun.Core.Routing;

public sealed class DomainRuleRouter : IRouter
{
    private readonly DomainPattern[] _patterns;

    public DomainRuleRouter(IEnumerable<DomainInterceptRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _patterns = [.. rules.Select(rule => DomainPattern.Parse(rule.Pattern, rule.OutboundName))];
    }

    public ValueTask<RouteDecision> RouteAsync(ConnectionContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var pattern in _patterns)
        {
            if (pattern.IsMatch(context.Host))
            {
                return ValueTask.FromResult(RouteDecision.Through(pattern.OutboundName));
            }
        }

        return ValueTask.FromResult(RouteDecision.Direct());
    }

    private readonly record struct DomainPattern(string Value, bool IsWildcard, string OutboundName)
    {
        public static DomainPattern Parse(string pattern, string outboundName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
            ArgumentException.ThrowIfNullOrWhiteSpace(outboundName);

            var normalized = pattern.Trim().TrimEnd('.').ToLowerInvariant();
            return normalized.StartsWith("*.", StringComparison.Ordinal)
                ? new DomainPattern(normalized[2..], IsWildcard: true, outboundName.Trim())
                : new DomainPattern(normalized, IsWildcard: false, outboundName.Trim());
        }

        public bool IsMatch(string host)
            => IsWildcard
                ? host.Equals(Value, StringComparison.OrdinalIgnoreCase) || host.EndsWith($".{Value}", StringComparison.OrdinalIgnoreCase)
                : host.Equals(Value, StringComparison.OrdinalIgnoreCase);
    }
}
