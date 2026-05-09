using System.Net;
using DotnetTun.Core.Dns;
using Xunit;

namespace DotnetTun.Core.Tests.Dns;

public sealed class FakeDnsMessageTests
{
    [Fact]
    public void TryReadQuestion_WithAQuery_ReturnsDomainAndTransactionId()
    {
        // Arrange
        byte[] query = DnsTestPackets.CreateAQuery(0x1234, "api.anthropic.com");

        // Act
        bool parsed = FakeDnsMessage.TryReadQuestion(query, out DnsQuestion question);

        // Assert
        Assert.True(parsed);
        Assert.Equal(0x1234, question.TransactionId);
        Assert.Equal("api.anthropic.com", question.Domain);
        Assert.Equal(DnsRecordType.A, question.RecordType);
    }

    [Fact]
    public void CreateAResponse_WithFakeIp_ReturnsParseableDnsAnswer()
    {
        // Arrange
        byte[] query = DnsTestPackets.CreateAQuery(0x1234, "api.anthropic.com");
        Assert.True(FakeDnsMessage.TryReadQuestion(query, out DnsQuestion question));

        // Act
        byte[] response = FakeDnsMessage.CreateAResponse(question, IPAddress.Parse("198.18.0.1"));

        // Assert
        Assert.Equal(0x12, response[0]);
        Assert.Equal(0x34, response[1]);
        Assert.Equal(0x81, response[2]);
        Assert.Equal(0x80, response[3]);
        Assert.Equal(0x00, response[6]);
        Assert.Equal(0x01, response[7]);
        Assert.Equal([198, 18, 0, 1], response[^4..]);
    }
}

internal static class DnsTestPackets
{
    public static byte[] CreateAQuery(ushort transactionId, string domain)
    {
        using var stream = new MemoryStream();
        stream.WriteByte((byte)(transactionId >> 8));
        stream.WriteByte((byte)(transactionId & 0xFF));
        stream.Write([0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);

        foreach (string label in domain.Split('.'))
        {
            stream.WriteByte((byte)label.Length);
            stream.Write(System.Text.Encoding.ASCII.GetBytes(label));
        }

        stream.WriteByte(0x00);
        stream.Write([0x00, 0x01, 0x00, 0x01]);
        return stream.ToArray();
    }
}
