namespace DotnetTun.Platforms.Windows.Networking;

public sealed record WindowsTunOpenResult(bool Success, nint AdapterHandle, string? InterfaceName, int ErrorCode, string? Message)
{
    public static WindowsTunOpenResult Failed(int errorCode, string message) => new(false, 0, null, errorCode, message);

    public static WindowsTunOpenResult Opened(nint adapterHandle, string interfaceName) => new(true, adapterHandle, interfaceName, 0, null);
}
