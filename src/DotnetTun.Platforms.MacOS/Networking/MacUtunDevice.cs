using System.Text;

namespace DotnetTun.Platforms.MacOS.Networking;

public sealed class MacUtunDevice
{
    private readonly IUtunNativeApi _nativeApi;

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
}
