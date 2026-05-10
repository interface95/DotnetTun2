namespace DotnetTun.Platforms.Windows.Networking;

public sealed class WindowsTunDevice(IWindowsTunNativeApi nativeApi)
{
    public Task<WindowsTunOpenResult> OpenAsync(string adapterName = "DotnetTun", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(adapterName))
        {
            throw new ArgumentException("Windows TUN adapter name must not be empty.", nameof(adapterName));
        }

        return Task.FromResult(nativeApi.OpenAdapter(adapterName.Trim()));
    }
}
