using System.Net;
using System.Net.Sockets;
using DotnetTun.Demo.Cli;
using Xunit;

namespace DotnetTun.Demo.Cli.Tests;

public sealed class DnsCommandTests
{
    [Fact]
    public async Task RunAsync_WithDnsCommand_RespondsWithFakeIp()
    {
        // Arrange
        var command = DotnetTunDemoCommand.Parse(["dns", "--listen", "127.0.0.1:0", "--domain", "api.anthropic.com"]);
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        // Act
        await using DotnetTunDemoCommandHandle handle = await command.StartAsync(cancellationToken);

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        byte[] query = CreateAQuery(0xCAFE, "api.anthropic.com");
        await udp.SendAsync(query, new IPEndPoint(IPAddress.Loopback, handle.Port), cancellationToken);
        UdpReceiveResult result = await udp.ReceiveAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);

        // Assert
        Assert.Equal([198, 18, 0, 1], result.Buffer[^4..]);
    }

    private static byte[] CreateAQuery(ushort transactionId, string domain)
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
