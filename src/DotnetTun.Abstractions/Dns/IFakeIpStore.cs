using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace DotnetTun.Abstractions.Dns;

public interface IFakeIpStore
{
    IPAddress Allocate(string domain);

    bool TryResolve(IPAddress fakeIp, [NotNullWhen(true)] out string? domain);
}
