namespace DotnetTun.Platforms.Linux.Networking;

public sealed record LinuxTunOpenResult(bool Success, int FileDescriptor, string? InterfaceName, int ErrorNumber)
{
    public static LinuxTunOpenResult Failed(int errorNumber) => new(false, -1, null, errorNumber);

    public static LinuxTunOpenResult Opened(int fileDescriptor, string interfaceName) => new(true, fileDescriptor, interfaceName, 0);
}
