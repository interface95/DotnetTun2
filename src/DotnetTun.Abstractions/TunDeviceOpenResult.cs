namespace DotnetTun.Abstractions;

public sealed record TunDeviceOpenResult(bool Success, int FileDescriptor, string? InterfaceName, int ErrorNumber)
{
    public static TunDeviceOpenResult Failed(int errorNumber) => new(false, -1, null, errorNumber);

    public static TunDeviceOpenResult Opened(int fileDescriptor, string interfaceName) => new(true, fileDescriptor, interfaceName, 0);
}
