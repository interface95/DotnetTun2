using System.Collections.Concurrent;
using System.Text;
using DotnetTun.Abstractions;

namespace DotnetTun.Platforms.MacOS.Networking;

public sealed class MacUtunDevice : ITunDevice
{
    private readonly IUtunNativeApi _nativeApi;
    private readonly ConcurrentDictionary<int, byte> _cancellationClosedFileDescriptors = new();

    public MacUtunDevice(IUtunNativeApi nativeApi)
    {
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
    }

    public Task<MacUtunOpenResult> OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Span<byte> interfaceNameBuffer = stackalloc byte[256];
        int fileDescriptor = _nativeApi.OpenUtun(-1, interfaceNameBuffer, out int errorNumber);
        if (fileDescriptor < 0)
        {
            return Task.FromResult(MacUtunOpenResult.Failed(errorNumber));
        }

        int nullIndex = interfaceNameBuffer.IndexOf((byte)0);
        ReadOnlySpan<byte> interfaceNameBytes = nullIndex >= 0 ? interfaceNameBuffer[..nullIndex] : interfaceNameBuffer;
        string interfaceName = Encoding.ASCII.GetString(interfaceNameBytes).Trim();
        if (string.IsNullOrWhiteSpace(interfaceName))
        {
            return Task.FromResult(MacUtunOpenResult.Failed(errorNumber));
        }

        return Task.FromResult(MacUtunOpenResult.Opened(fileDescriptor, interfaceName));
    }

    public async Task<TunDeviceOpenResult> OpenTunAsync(CancellationToken cancellationToken = default)
    {
        MacUtunOpenResult result = await OpenAsync(cancellationToken);
        if (!result.Success || string.IsNullOrWhiteSpace(result.InterfaceName))
        {
            return TunDeviceOpenResult.Failed(result.ErrorNumber);
        }

        return TunDeviceOpenResult.Opened(result.FileDescriptor, result.InterfaceName);
    }

    public ValueTask<TunPacketIoResult> ReadPacketAsync(int fileDescriptor, Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var cancellationRegistration = cancellationToken.Register(
            static state =>
            {
                var cancellationState = (ReadCancellationState)state!;
                if (cancellationState.NativeApi.Close(cancellationState.FileDescriptor, out _) == 0)
                {
                    cancellationState.CancellationClosedFileDescriptors[cancellationState.FileDescriptor] = 0;
                }
            },
            new ReadCancellationState(_nativeApi, _cancellationClosedFileDescriptors, fileDescriptor));

        var bytesTransferred = _nativeApi.ReadPacket(fileDescriptor, buffer.Span, out var errorNumber);
        if (bytesTransferred < 0 && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        var result = bytesTransferred < 0
            ? TunPacketIoResult.Failed(errorNumber)
            : TunPacketIoResult.Transferred(bytesTransferred);

        return ValueTask.FromResult(result);
    }

    public ValueTask<TunPacketIoResult> WritePacketAsync(int fileDescriptor, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        int bytesTransferred = _nativeApi.WritePacket(fileDescriptor, packet.Span, out int errorNumber);
        TunPacketIoResult result = bytesTransferred < 0
            ? TunPacketIoResult.Failed(errorNumber)
            : TunPacketIoResult.Transferred(bytesTransferred);

        return ValueTask.FromResult(result);
    }

    public ValueTask<TunDeviceCloseResult> CloseTunAsync(int fileDescriptor, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_cancellationClosedFileDescriptors.TryRemove(fileDescriptor, out _))
        {
            return ValueTask.FromResult(TunDeviceCloseResult.Closed());
        }

        int closeResult = _nativeApi.Close(fileDescriptor, out int errorNumber);
        TunDeviceCloseResult result = closeResult < 0
            ? TunDeviceCloseResult.Failed(errorNumber)
            : TunDeviceCloseResult.Closed();

        return ValueTask.FromResult(result);
    }

    private sealed record ReadCancellationState(
        IUtunNativeApi NativeApi,
        ConcurrentDictionary<int, byte> CancellationClosedFileDescriptors,
        int FileDescriptor);
}
