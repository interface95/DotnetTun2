namespace DotnetTun.Abstractions;

public interface ITunDevice : IAsyncDisposable
{
    bool IsOpen { get; }

    string? InterfaceName { get; }

    ValueTask OpenAsync(CancellationToken cancellationToken = default);

    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default);

    ValueTask CloseAsync(CancellationToken cancellationToken = default);
}
