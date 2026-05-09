using System.Net;

namespace DotnetTun.Abstractions.Routing;

public sealed record InterceptDecision(string Domain, bool Intercepted, IPAddress? FakeIp)
{
    public static InterceptDecision Direct(string domain) => new(domain, false, null);

    public static InterceptDecision Intercept(string domain, IPAddress fakeIp) => new(domain, true, fakeIp);
}
