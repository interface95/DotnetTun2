using System.Net;
using System.Net.Sockets;
using DotnetTun.Abstractions.Routing;
using DotnetTun.Core.Dns;
using DotnetTun.Core.Routing;
using Xunit;

namespace DotnetTun.Core.Tests.Dns;

public sealed class FakeDnsServerTests
{
    [Fact]
    public async Task StartAsync_WithInterceptedQuery_ReturnsFakeIpUdpResponse()
    {
        // Arrange
        var pool = new FakeIpPool(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.254"));
        var router = new DomainInterceptRouter([new DomainInterceptRule("api.anthropic.com")], pool);
        var resolver = new FakeDnsResolver(router);
        await using var server = new FakeDnsServer(resolver, IPAddress.Loopback, port: 0);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await server.StartAsync(cancellationToken);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        byte[] query = DnsTestPackets.CreateAQuery(0xBEEF, "api.anthropic.com");

        // Act
        await udp.SendAsync(query, new IPEndPoint(IPAddress.Loopback, server.Port), cancellationToken);
        UdpReceiveResult result = await udp.ReceiveAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);

        // Assert
        Assert.Equal([198, 18, 0, 1], result.Buffer[^4..]);
    }
}
