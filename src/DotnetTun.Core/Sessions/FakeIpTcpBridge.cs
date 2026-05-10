using System.Net;
using DotnetTun.Abstractions;
using DotnetTun.Core.Dns;

namespace DotnetTun.Core.Sessions;

public sealed class FakeIpTcpBridge(FakeIpPool fakeIpPool, IOutbound outbound)
{
    public async Task BridgeAsync(Stream clientStream, IPAddress fakeIp, int port, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clientStream);
        ArgumentNullException.ThrowIfNull(fakeIp);

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Target port must be between 1 and 65535.");
        }

        if (!fakeIpPool.TryResolve(fakeIp, out string? domain))
        {
            throw new InvalidOperationException($"No domain lease exists for fake IP {fakeIp}.");
        }

        await using Stream outboundStream = await outbound.ConnectAsync(domain, port, cancellationToken).ConfigureAwait(false);
        await BridgeStreamsAsync(clientStream, outboundStream, cancellationToken).ConfigureAwait(false);
    }

    private static async Task BridgeStreamsAsync(Stream left, Stream right, CancellationToken cancellationToken)
    {
        Task leftToRight = CopyAndCompleteAsync(left, right, cancellationToken);
        Task rightToLeft = CopyAndCompleteAsync(right, left, cancellationToken);

        await Task.WhenAll(leftToRight, rightToLeft).ConfigureAwait(false);
    }

    private static async Task CopyAndCompleteAsync(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

}
