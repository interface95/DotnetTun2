using System.Net;
using DotnetTun.Abstractions.Diagnostics;

namespace DotnetTun.Demo.Cli;

internal sealed class ConsoleProxyLogger : IProxyLogger
{
    public void DnsIntercepted(string domain, IPAddress fakeIp) => Console.WriteLine($"[DNS] intercepted {domain} -> {fakeIp}");

    public void DnsBypassed(string domain) => Console.WriteLine($"[DNS] bypass {domain}");

    public void DnsInvalidQuery(int bytesReceived) => Console.WriteLine($"[DNS] invalid query {bytesReceived} bytes");
}
