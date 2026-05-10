namespace DotnetTun.Abstractions;

public interface ITunDevice
{
    Task<TunDeviceOpenResult> OpenTunAsync(CancellationToken cancellationToken = default);

    ValueTask<TunPacketIoResult> ReadPacketAsync(int fileDescriptor, Memory<byte> buffer, CancellationToken cancellationToken = default);

    ValueTask<TunPacketIoResult> WritePacketAsync(int fileDescriptor, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default);

    ValueTask<TunDeviceCloseResult> CloseTunAsync(int fileDescriptor, CancellationToken cancellationToken = default);
}
