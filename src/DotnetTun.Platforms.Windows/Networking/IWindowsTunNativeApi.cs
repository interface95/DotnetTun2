namespace DotnetTun.Platforms.Windows.Networking;

public interface IWindowsTunNativeApi
{
    WindowsTunOpenResult OpenAdapter(string adapterName);
}
