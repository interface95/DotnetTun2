namespace DotnetTun.Core.Sessions;

public interface ITunPacketHandler
{
    ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default);
}
