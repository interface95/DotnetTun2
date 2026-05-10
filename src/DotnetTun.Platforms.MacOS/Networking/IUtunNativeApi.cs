namespace DotnetTun.Platforms.MacOS.Networking;

public interface IUtunNativeApi
{
    int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber);

    int ReadPacket(int fileDescriptor, Span<byte> buffer, out int errorNumber);

    int WritePacket(int fileDescriptor, ReadOnlySpan<byte> packet, out int errorNumber);

    int Close(int fileDescriptor, out int errorNumber);
}
