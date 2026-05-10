namespace DotnetTun.Abstractions;

public interface IOutbound
{
    ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default);
}
