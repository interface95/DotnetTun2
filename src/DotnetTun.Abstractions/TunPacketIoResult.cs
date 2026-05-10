namespace DotnetTun.Abstractions;

public sealed record TunPacketIoResult(bool Success, int BytesTransferred, int ErrorNumber)
{
    public static TunPacketIoResult Failed(int errorNumber) => new(false, 0, errorNumber);

    public static TunPacketIoResult Transferred(int bytesTransferred) => new(true, bytesTransferred, 0);
}
