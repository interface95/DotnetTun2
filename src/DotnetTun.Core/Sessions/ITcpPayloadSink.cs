namespace DotnetTun.Core.Sessions;

public interface ITcpPayloadSink
{
    ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> WriteAsync(TcpSession session, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default);

    ValueTask CloseAsync(TcpSession session, CancellationToken cancellationToken = default);
}
