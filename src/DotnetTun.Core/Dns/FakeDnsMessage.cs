using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DotnetTun.Core.Dns;

public static class FakeDnsMessage
{
    private const int HeaderLength = 12;
    private const ushort DnsClassInternet = 1;

    public static bool TryReadQuestion(ReadOnlySpan<byte> packet, out DnsQuestion question)
    {
        question = new DnsQuestion(0, string.Empty, DnsRecordType.A, []);

        if (packet.Length < HeaderLength)
        {
            return false;
        }

        ushort transactionId = BinaryPrimitives.ReadUInt16BigEndian(packet[..2]);
        ushort questionCount = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(4, 2));
        if (questionCount != 1)
        {
            return false;
        }

        int offset = HeaderLength;
        List<string> labels = [];
        while (offset < packet.Length)
        {
            byte labelLength = packet[offset++];
            if (labelLength == 0)
            {
                break;
            }

            if ((labelLength & 0b1100_0000) != 0 || offset + labelLength > packet.Length)
            {
                return false;
            }

            labels.Add(Encoding.ASCII.GetString(packet.Slice(offset, labelLength)));
            offset += labelLength;
        }

        if (labels.Count == 0 || offset + 4 > packet.Length)
        {
            return false;
        }

        ushort type = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset, 2));
        ushort dnsClass = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(offset + 2, 2));
        if (dnsClass != DnsClassInternet || !Enum.IsDefined(typeof(DnsRecordType), type))
        {
            return false;
        }

        int questionLength = offset + 4 - HeaderLength;
        byte[] originalQuestion = packet.Slice(HeaderLength, questionLength).ToArray();
        question = new DnsQuestion(transactionId, string.Join('.', labels).ToLowerInvariant(), (DnsRecordType)type, originalQuestion);
        return true;
    }

    public static byte[] CreateAResponse(DnsQuestion question, IPAddress fakeIp, int ttlSeconds = 60)
    {
        if (question.RecordType != DnsRecordType.A)
        {
            throw new ArgumentException("Only A responses are supported.", nameof(question));
        }

        if (fakeIp.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("A responses require an IPv4 address.", nameof(fakeIp));
        }

        byte[] response = new byte[HeaderLength + question.OriginalQuestion.Length + 16];
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), question.TransactionId);
        response[2] = 0x81;
        response[3] = 0x80;
        response[4] = 0x00;
        response[5] = 0x01;
        response[6] = 0x00;
        response[7] = 0x01;

        question.OriginalQuestion.CopyTo(response.AsSpan(HeaderLength));
        int answerOffset = HeaderLength + question.OriginalQuestion.Length;

        response[answerOffset] = 0xC0;
        response[answerOffset + 1] = 0x0C;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(answerOffset + 2, 2), (ushort)DnsRecordType.A);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(answerOffset + 4, 2), DnsClassInternet);
        BinaryPrimitives.WriteUInt32BigEndian(response.AsSpan(answerOffset + 6, 4), (uint)ttlSeconds);
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(answerOffset + 10, 2), 4);
        fakeIp.GetAddressBytes().CopyTo(response.AsSpan(answerOffset + 12, 4));

        return response;
    }

    public static byte[] CreateNoDataResponse(DnsQuestion question)
    {
        byte[] response = new byte[HeaderLength + question.OriginalQuestion.Length];
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(0, 2), question.TransactionId);
        response[2] = 0x81;
        response[3] = 0x80;
        response[4] = 0x00;
        response[5] = 0x01;
        response[6] = 0x00;
        response[7] = 0x00;
        response[8] = 0x00;
        response[9] = 0x00;
        response[10] = 0x00;
        response[11] = 0x00;

        question.OriginalQuestion.CopyTo(response.AsSpan(HeaderLength));
        return response;
    }
}
