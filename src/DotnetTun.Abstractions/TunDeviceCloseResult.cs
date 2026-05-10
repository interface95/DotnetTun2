namespace DotnetTun.Abstractions;

public sealed record TunDeviceCloseResult(bool Success, int ErrorNumber)
{
    public static TunDeviceCloseResult Failed(int errorNumber) => new(false, errorNumber);

    public static TunDeviceCloseResult Closed() => new(true, 0);
}
