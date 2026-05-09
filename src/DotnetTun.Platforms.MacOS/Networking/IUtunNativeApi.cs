namespace DotnetTun.Platforms.MacOS.Networking;

public interface IUtunNativeApi
{
    int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber);
}
