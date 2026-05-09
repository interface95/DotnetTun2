using DotnetTun.Abstractions.Routing;
using DotnetTun.Core.Dns;

namespace DotnetTun.Core.Routing;

public sealed class DomainInterceptRouter(IEnumerable<DomainInterceptRule> rules, FakeIpPool fakeIpPool)
{
    private readonly DomainPattern[] _patterns = rules.Select(rule => DomainPattern.Parse(rule.Pattern)).ToArray();

    public InterceptDecision Decide(string domain)
    {
        string normalizedDomain = NormalizeDomain(domain);
        bool matched = _patterns.Any(pattern => pattern.IsMatch(normalizedDomain));
        if (!matched)
        {
            return InterceptDecision.Direct(normalizedDomain);
        }

        var lease = fakeIpPool.Allocate(normalizedDomain);
        return InterceptDecision.Intercept(normalizedDomain, lease.FakeIp);
    }

    private static string NormalizeDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new ArgumentException("Domain must not be empty.", nameof(domain));
        }

        return domain.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private readonly record struct DomainPattern(string Value, bool IsWildcard)
    {
        public static DomainPattern Parse(string pattern)
        {
            string normalized = NormalizeDomain(pattern);
            if (normalized.StartsWith("*.", StringComparison.Ordinal))
            {
                return new DomainPattern(normalized[2..], true);
            }

            return new DomainPattern(normalized, false);
        }

        public bool IsMatch(string domain)
            => IsWildcard
                ? domain.EndsWith($".{Value}", StringComparison.OrdinalIgnoreCase)
                : string.Equals(domain, Value, StringComparison.OrdinalIgnoreCase);
    }
}
