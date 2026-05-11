namespace DotnetTun.Abstractions;

public interface IOutbound
{
    string Name { get; }

    ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default);
}
