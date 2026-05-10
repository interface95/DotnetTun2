namespace DotnetTun.Platforms.Linux.Networking;

public sealed class LinuxTunDevice(ILinuxTunNativeApi nativeApi)
{
    public Task<LinuxTunOpenResult> OpenAsync(string requestedName = "tun%d", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(requestedName))
        {
            throw new ArgumentException("Linux TUN interface name must not be empty.", nameof(requestedName));
        }

        int fileDescriptor = nativeApi.OpenTun(requestedName.Trim(), out string? interfaceName, out int errorNumber);
        if (fileDescriptor < 0 || string.IsNullOrWhiteSpace(interfaceName))
        {
            return Task.FromResult(LinuxTunOpenResult.Failed(errorNumber));
        }

        return Task.FromResult(LinuxTunOpenResult.Opened(fileDescriptor, interfaceName));
    }
}
