namespace DotnetTun.Platforms.Linux.Networking;

public interface ILinuxTunNativeApi
{
    int OpenTun(string requestedName, out string? interfaceName, out int errorNumber);

    int Close(int fileDescriptor);
}
