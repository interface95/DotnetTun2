using System.Buffers.Binary;
using System.Text;

namespace DotnetTun.Core.Benchmarks.Support;

internal static class BenchmarkDnsQueryBuilder
{
    public static byte[] BuildAQuery(string domain, ushort id)
    {
        using MemoryStream stream = new();
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        header[2] = 0x01;
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);
        stream.Write(header);

        foreach (var label in domain.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
        Span<byte> questionTail = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(questionTail, 1);
        BinaryPrimitives.WriteUInt16BigEndian(questionTail[2..], 1);
        stream.Write(questionTail);

        return stream.ToArray();
    }
}
