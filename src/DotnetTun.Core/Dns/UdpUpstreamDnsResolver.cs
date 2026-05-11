using System.Net;
using System.Net.Sockets;

namespace DotnetTun.Core.Dns;

public sealed class UdpUpstreamDnsResolver : IUpstreamDnsResolver
{
    private readonly IPEndPoint _upstream;
    private readonly TimeSpan _timeout;

    public UdpUpstreamDnsResolver(IPEndPoint upstream, TimeSpan timeout)
    {
        _upstream = upstream ?? throw new ArgumentNullException(nameof(upstream));
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive.");
        }

        _timeout = timeout;
    }

    public async ValueTask<byte[]?> ResolveAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using UdpClient client = new(_upstream.AddressFamily);
        using var timeoutSource = new CancellationTokenSource(_timeout);
        using var linkedSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await client.SendAsync(query, _upstream, linkedSource.Token).ConfigureAwait(false);
            var result = await client.ReceiveAsync(linkedSource.Token).ConfigureAwait(false);
            return result.Buffer;
        }
        catch (OperationCanceledException) when (timeoutSource.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
