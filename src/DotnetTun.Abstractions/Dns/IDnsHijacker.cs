namespace DotnetTun.Abstractions.Dns;

public interface IDnsHijacker
{
    ValueTask<DnsHandlingResult> HandleAsync(ReadOnlyMemory<byte> query, CancellationToken cancellationToken = default);
}
