using System.Runtime.InteropServices;
using System.Text;

namespace DotnetTun.Platforms.Linux.Networking;

public sealed class LinuxTunNativeApi : ILinuxTunNativeApi
{
    private const int OpenReadWrite = 0x0002;
    private const int InterfaceNameSize = 16;
    private const int IfReqSize = 40;
    private const short InterfaceFlagTun = 0x0001;
    private const short InterfaceFlagNoPacketInfo = 0x1000;
    private const ulong TunSetInterface = 0x400454ca;

    public int OpenTun(string requestedName, out string? interfaceName, out int errorNumber)
    {
        interfaceName = null;
        int fileDescriptor = open("/dev/net/tun", OpenReadWrite);
        if (fileDescriptor < 0)
        {
            errorNumber = Marshal.GetLastPInvokeError();
            return -1;
        }

        byte[] ifReq = CreateIfReq(requestedName);
        if (ioctl(fileDescriptor, TunSetInterface, ifReq) < 0)
        {
            errorNumber = Marshal.GetLastPInvokeError();
            _ = close(fileDescriptor);
            return -1;
        }

        interfaceName = ReadInterfaceName(ifReq);
        errorNumber = 0;
        return fileDescriptor;
    }

    public int Close(int fileDescriptor) => close(fileDescriptor);

    private static byte[] CreateIfReq(string requestedName)
    {
        byte[] ifReq = new byte[IfReqSize];
        byte[] nameBytes = Encoding.ASCII.GetBytes(requestedName);
        if (nameBytes.Length >= InterfaceNameSize)
        {
            throw new ArgumentException($"Linux TUN interface name must be shorter than {InterfaceNameSize} bytes.", nameof(requestedName));
        }

        nameBytes.CopyTo(ifReq, 0);
        short flags = InterfaceFlagTun | InterfaceFlagNoPacketInfo;
        ifReq[InterfaceNameSize] = (byte)(flags & 0xFF);
        ifReq[InterfaceNameSize + 1] = (byte)((flags >> 8) & 0xFF);
        return ifReq;
    }

    private static string ReadInterfaceName(byte[] ifReq)
    {
        int length = Array.IndexOf(ifReq, (byte)0, 0, InterfaceNameSize);
        if (length < 0)
        {
            length = InterfaceNameSize;
        }

        return Encoding.ASCII.GetString(ifReq, 0, length).Trim();
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int open(string pathName, int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    private static extern int ioctl(int fileDescriptor, ulong request, byte[] ifReq);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    private static extern int close(int fileDescriptor);
}
