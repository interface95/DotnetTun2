namespace DotnetTun.Core.Benchmarks.Support;

internal static class BenchmarkPayload
{
    public static byte[] Create(int size)
    {
        var payload = new byte[size];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = (byte)(index % 251);
        }

        return payload;
    }
}
