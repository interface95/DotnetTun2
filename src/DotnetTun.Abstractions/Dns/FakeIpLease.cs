using System.Net;

namespace DotnetTun.Abstractions.Dns;

public sealed record FakeIpLease(string Domain, IPAddress FakeIp);
