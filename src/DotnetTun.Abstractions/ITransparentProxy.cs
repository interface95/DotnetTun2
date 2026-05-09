namespace DotnetTun.Abstractions;

public interface ITransparentProxy
{
    DotnetTunOptions Options { get; }

    IReadOnlyDictionary<string, IOutbound> Outbounds { get; }

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);
}
