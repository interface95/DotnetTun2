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

    [DllImport("libutunshim.dylib", CallingConvention = CallingConvention.Cdecl, EntryPoint = "open_utun")]
    private static extern int open_utun(int unit, nint ifnameBuffer, UIntPtr ifnameLength, out int errorNumber);
}
