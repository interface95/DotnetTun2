using System.Net;

namespace DotnetTun.Abstractions.Diagnostics;

public interface IProxyLogger
{
    void DnsIntercepted(string domain, IPAddress fakeIp);

    void DnsBypassed(string domain);

    void DnsInvalidQuery(int bytesReceived);
}
