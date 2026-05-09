using System.Net;

namespace DotnetTun.Abstractions.Diagnostics;

public sealed class NullProxyLogger : IProxyLogger
{
    public static NullProxyLogger Instance { get; } = new();

    private NullProxyLogger()
    {
    }

    public void DnsIntercepted(string domain, IPAddress fakeIp)
    {
    }

    public void DnsBypassed(string domain)
    {
    }

    public void DnsInvalidQuery(int bytesReceived)
    {
    }
}
