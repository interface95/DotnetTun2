using System.Runtime.InteropServices;

namespace DotnetTun.Platforms.MacOS.Networking;

public sealed class MacUtunNativeApi : IUtunNativeApi
{
    public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
    {
        unsafe
        {
            fixed (byte* buffer = interfaceNameBuffer)
            {
                return open_utun(unit, (nint)buffer, (UIntPtr)interfaceNameBuffer.Length, out errorNumber);
            }
        }
    }

    public int ReadPacket(int fileDescriptor, Span<byte> buffer, out int errorNumber)
    {
        unsafe
        {
            fixed (byte* pinnedBuffer = buffer)
            {
                nint bytesRead = read(fileDescriptor, (nint)pinnedBuffer, (UIntPtr)buffer.Length);
                if (bytesRead < 0)
                {
                    errorNumber = Marshal.GetLastPInvokeError();
                    return -1;
                }

                errorNumber = 0;
                return checked((int)bytesRead);
            }
        }
    }

    public int WritePacket(int fileDescriptor, ReadOnlySpan<byte> packet, out int errorNumber)
    {
        unsafe
        {
            fixed (byte* pinnedPacket = packet)
            {
                nint bytesWritten = write(fileDescriptor, (nint)pinnedPacket, (UIntPtr)packet.Length);
                if (bytesWritten < 0)
                {
                    errorNumber = Marshal.GetLastPInvokeError();
                    return -1;
                }

                errorNumber = 0;
                return checked((int)bytesWritten);
            }
        }
    }

    public int Close(int fileDescriptor, out int errorNumber)
    {
        int closeResult = close(fileDescriptor);
        if (closeResult < 0)
        {
            errorNumber = Marshal.GetLastPInvokeError();
            return -1;
        }

        errorNumber = 0;
        return closeResult;
    }

    [DllImport("libutunshim.dylib", CallingConvention = CallingConvention.Cdecl, EntryPoint = "open_utun")]
    private static extern int open_utun(int unit, nint ifnameBuffer, UIntPtr ifnameLength, out int errorNumber);

    [DllImport("libc", SetLastError = true, EntryPoint = "read")]
    private static extern nint read(int fileDescriptor, nint buffer, UIntPtr count);

    [DllImport("libc", SetLastError = true, EntryPoint = "write")]
    private static extern nint write(int fileDescriptor, nint buffer, UIntPtr count);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    private static extern int close(int fileDescriptor);
}
