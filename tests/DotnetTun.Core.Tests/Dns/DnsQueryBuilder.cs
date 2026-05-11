using System.Buffers.Binary;
using System.Text;

namespace DotnetTun.Core.Tests.Dns;

internal static class DnsQueryBuilder
{
    public static byte[] BuildAQuery(string domain, ushort id) => BuildQuery(domain, id, qtype: 1);

    public static byte[] BuildAaaaQuery(string domain, ushort id) => BuildQuery(domain, id, qtype: 28);

    private static byte[] BuildQuery(string domain, ushort id, ushort qtype)
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
        BinaryPrimitives.WriteUInt16BigEndian(questionTail, qtype);
        BinaryPrimitives.WriteUInt16BigEndian(questionTail[2..], 1);
        stream.Write(questionTail);

        return stream.ToArray();
    }
}
