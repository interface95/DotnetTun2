using DotnetTun.Abstractions.Dns;

namespace DotnetTun.Core;

public sealed record DotnetTunDryRunPlan(
    IReadOnlyList<FakeIpLease> ExactDomainLeases,
    IReadOnlyList<string> WildcardPatterns);
