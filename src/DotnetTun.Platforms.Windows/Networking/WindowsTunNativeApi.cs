namespace DotnetTun.Platforms.Windows.Networking;

public sealed class WindowsTunNativeApi : IWindowsTunNativeApi
{
    public WindowsTunOpenResult OpenAdapter(string adapterName)
        => WindowsTunOpenResult.Failed(
            errorCode: -1,
            message: "Wintun native adapter packaging is not implemented yet.");
}
