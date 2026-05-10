namespace DotnetTun.Core.Dns;

public interface IUpstreamDnsResolver
{
    ValueTask<byte[]?> ResolveAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken = default);
}
