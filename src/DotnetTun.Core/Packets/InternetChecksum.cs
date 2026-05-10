using System.Buffers.Binary;

namespace DotnetTun.Core.Packets;

public static class InternetChecksum
{
    public static ushort Compute(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int index = 0;
        while (index + 1 < data.Length)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(data[index..(index + 2)]);
            index += 2;
        }

        if (index < data.Length)
        {
            sum += (uint)(data[index] << 8);
        }

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        return (ushort)~sum;
    }

    public static bool IsValid(ReadOnlySpan<byte> data) => Compute(data) == 0;
}
