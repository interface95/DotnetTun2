namespace DotnetTun.Platforms.MacOS.Networking;

public sealed record MacUtunOpenResult(bool Success, int FileDescriptor, string? InterfaceName, int ErrorNumber)
{
    public static MacUtunOpenResult Failed(int errorNumber) => new(false, -1, null, errorNumber);

    public static MacUtunOpenResult Opened(int fileDescriptor, string interfaceName) => new(true, fileDescriptor, interfaceName, 0);
}
