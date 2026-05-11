using System.Buffers;
using System.Text;
using DotnetTun.Abstractions;

namespace DotnetTun.Platforms.MacOS.Networking;

public sealed class MacUtunDevice : ITunDevice
{
    private readonly IUtunNativeApi _nativeApi;
    private readonly object _lifecycleLock = new();
    private int? _fileDescriptor;
    private string? _interfaceName;

    public MacUtunDevice(IUtunNativeApi nativeApi)
    {
        _nativeApi = nativeApi ?? throw new ArgumentNullException(nameof(nativeApi));
    }

    public bool IsOpen
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _fileDescriptor is not null;
            }
        }
    }

    public string? InterfaceName
    {
        get
        {
            lock (_lifecycleLock)
            {
                return _interfaceName;
            }
        }
    }

    public ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsOpen)
        {
            return ValueTask.CompletedTask;
        }

        Span<byte> interfaceNameBuffer = stackalloc byte[256];
        var fileDescriptor = _nativeApi.OpenUtun(-1, interfaceNameBuffer, out var errorNumber);
        if (fileDescriptor < 0)
        {
            throw new IOException($"macOS utun open failed with error {errorNumber}.");
        }

        var interfaceName = ReadInterfaceName(interfaceNameBuffer);
        if (!IsValidUtunInterfaceName(interfaceName))
        {
            _ = _nativeApi.Close(fileDescriptor, out _);
            throw new IOException($"macOS utun open returned an invalid interface name '{interfaceName}'.");
        }

        var shouldCloseOpenedFileDescriptor = false;
        lock (_lifecycleLock)
        {
            if (_fileDescriptor is not null)
            {
                shouldCloseOpenedFileDescriptor = true;
            }
            else
            {
                _fileDescriptor = fileDescriptor;
                _interfaceName = interfaceName;
            }
        }

        if (shouldCloseOpenedFileDescriptor)
        {
            var closeResult = _nativeApi.Close(fileDescriptor, out var closeErrorNumber);
            if (closeResult < 0)
            {
                throw new IOException($"macOS utun close after concurrent open failed with error {closeErrorNumber}.");
            }

            return ValueTask.CompletedTask;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fileDescriptor = GetOpenFileDescriptor();

        using var cancellationRegistration = cancellationToken.CanBeCanceled
            ? cancellationToken.Register(
                static state =>
                {
                    var cancellationState = (ReadCancellationState)state!;
                    if (cancellationState.Device.TryClaimOpenFileDescriptor(cancellationState.FileDescriptor))
                    {
                        _ = cancellationState.NativeApi.Close(cancellationState.FileDescriptor, out _);
                    }
                },
                new ReadCancellationState(_nativeApi, this, fileDescriptor))
            : default;

        var nativeBufferLength = buffer.Length + MacUtunFrame.HeaderLength;
        var nativeBuffer = ArrayPool<byte>.Shared.Rent(nativeBufferLength);
        try
        {
            var bytesTransferred = _nativeApi.ReadPacket(fileDescriptor, nativeBuffer.AsSpan(0, nativeBufferLength), out var errorNumber);
            if (bytesTransferred < 0 && cancellationToken.IsCancellationRequested)
            {
                _ = TryClaimOpenFileDescriptor(fileDescriptor);
                throw new OperationCanceledException(cancellationToken);
            }

            if (bytesTransferred < 0)
            {
                throw new IOException($"TUN packet read failed with error {errorNumber}.");
            }

            if (bytesTransferred > nativeBufferLength)
            {
                throw new InvalidOperationException("TUN packet read returned more bytes than the supplied buffer can hold.");
            }

            if (bytesTransferred < MacUtunFrame.HeaderLength)
            {
                throw new InvalidDataException("utun packet read returned a short address-family header.");
            }

            var frame = nativeBuffer.AsSpan(0, bytesTransferred);
            var addressFamily = MacUtunFrame.ReadAddressFamily(frame);
            if (!MacUtunFrame.IsSupportedAddressFamily(addressFamily))
            {
                throw new InvalidDataException($"utun packet read returned unsupported address family {addressFamily}.");
            }

            if (!MacUtunFrame.TryReadPayload(frame, buffer.Span, out var payloadBytesTransferred))
            {
                throw new InvalidOperationException("TUN packet read could not strip the utun address-family header.");
            }

            return ValueTask.FromResult(payloadBytesTransferred);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(nativeBuffer);
        }
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var fileDescriptor = GetOpenFileDescriptor();

        var framedPacketLength = packet.Length + MacUtunFrame.HeaderLength;
        var framedPacket = ArrayPool<byte>.Shared.Rent(framedPacketLength);
        try
        {
            var framedPacketSpan = framedPacket.AsSpan(0, framedPacketLength);
            if (!MacUtunFrame.TryWriteFrame(packet.Span, framedPacketSpan, out var bytesWritten))
            {
                throw new InvalidDataException("Only IPv4 and IPv6 packets can be written to a utun device.");
            }

            var bytesTransferred = _nativeApi.WritePacket(fileDescriptor, framedPacketSpan[..bytesWritten], out var errorNumber);
            if (bytesTransferred < 0)
            {
                throw new IOException($"TUN packet write failed with error {errorNumber}.");
            }

            if (bytesTransferred != bytesWritten)
            {
                throw new IOException($"TUN packet write failed with partial framed write: wrote {bytesTransferred} of {bytesWritten} bytes.");
            }

            return ValueTask.CompletedTask;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(framedPacket);
        }
    }

    public ValueTask CloseAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!TryClaimOpenFileDescriptor(out var fileDescriptor))
        {
            return ValueTask.CompletedTask;
        }

        var closeResult = _nativeApi.Close(fileDescriptor, out var errorNumber);
        if (closeResult < 0)
        {
            throw new IOException($"macOS utun close failed with error {errorNumber}.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => CloseAsync(CancellationToken.None);

    private int GetOpenFileDescriptor()
    {
        lock (_lifecycleLock)
        {
            return _fileDescriptor ?? throw new InvalidOperationException("TUN device is not open.");
        }
    }

    private bool TryClaimOpenFileDescriptor(out int fileDescriptor)
    {
        lock (_lifecycleLock)
        {
            if (_fileDescriptor is not { } currentFileDescriptor)
            {
                fileDescriptor = -1;
                return false;
            }

            _fileDescriptor = null;
            _interfaceName = null;
            fileDescriptor = currentFileDescriptor;
            return true;
        }
    }

    private bool TryClaimOpenFileDescriptor(int expectedFileDescriptor)
    {
        lock (_lifecycleLock)
        {
            if (_fileDescriptor != expectedFileDescriptor)
            {
                return false;
            }

            _fileDescriptor = null;
            _interfaceName = null;
            return true;
        }
    }

    private sealed record ReadCancellationState(
        IUtunNativeApi NativeApi,
        MacUtunDevice Device,
        int FileDescriptor);

    private static string ReadInterfaceName(ReadOnlySpan<byte> interfaceNameBuffer)
    {
        var nullIndex = interfaceNameBuffer.IndexOf((byte)0);
        var interfaceNameBytes = nullIndex >= 0 ? interfaceNameBuffer[..nullIndex] : interfaceNameBuffer;
        return Encoding.ASCII.GetString(interfaceNameBytes).Trim();
    }

    private static bool IsValidUtunInterfaceName(string interfaceName)
    {
        const string prefix = "utun";
        if (!interfaceName.StartsWith(prefix, StringComparison.Ordinal) || interfaceName.Length == prefix.Length)
        {
            return false;
        }

        foreach (var character in interfaceName.AsSpan(prefix.Length))
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return true;
    }

}
