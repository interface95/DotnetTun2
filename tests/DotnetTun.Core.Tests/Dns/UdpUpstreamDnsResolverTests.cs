using System.Net;
using System.Net.Sockets;
using DotnetTun.Core.Dns;
using Xunit;

namespace DotnetTun.Core.Tests.Dns;

public sealed class UdpUpstreamDnsResolverTests
{
    [Fact]
    public async Task ResolveAsync_RoundTripsThroughLocalUdpServer()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var resolver = new UdpUpstreamDnsResolver(serverEndpoint, TimeSpan.FromSeconds(2));

        var resolveTask = resolver.ResolveAsync(new byte[] { 0x12, 0x34 }, TestContext.Current.CancellationToken).AsTask();
        var received = await server.ReceiveAsync(TestContext.Current.CancellationToken);
        await server.SendAsync(new byte[] { 0xAB, 0xCD }, received.RemoteEndPoint, TestContext.Current.CancellationToken);

        var response = await resolveTask;

        Assert.Equal([0xAB, 0xCD], response);
    }

    [Fact]
    public async Task ResolveAsync_WhenUpstreamDoesNotReply_ReturnsNullAfterTimeout()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var resolver = new UdpUpstreamDnsResolver(serverEndpoint, TimeSpan.FromMilliseconds(25));

        var response = await resolver.ResolveAsync(new byte[] { 0x12, 0x34 }, TestContext.Current.CancellationToken);

        Assert.Null(response);
    }

    [Fact]
    public async Task ResolveAsync_WhenCancelled_ThrowsOperationCanceledException()
    {
        using UdpClient server = new(new IPEndPoint(IPAddress.Loopback, 0));
        var serverEndpoint = (IPEndPoint)server.Client.LocalEndPoint!;
        var resolver = new UdpUpstreamDnsResolver(serverEndpoint, TimeSpan.FromSeconds(5));
        using var cancellationSource = new CancellationTokenSource();
        await cancellationSource.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await resolver.ResolveAsync(new byte[] { 0x12, 0x34 }, cancellationSource.Token));
    }
}
