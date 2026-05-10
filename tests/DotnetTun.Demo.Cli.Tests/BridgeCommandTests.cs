using System.Net;
using System.Net.Sockets;
using DotnetTun.Demo.Cli;
using Xunit;

namespace DotnetTun.Demo.Cli.Tests;

public sealed class BridgeCommandTests
{
    [Fact]
    public async Task RunAsync_WithBridgeCommand_RelaysLocalTcpThroughSocks5Outbound()
    {
        // Arrange
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var socksListener = new TcpListener(IPAddress.Loopback, port: 0);
        socksListener.Start();
        int socksPort = ((IPEndPoint)socksListener.LocalEndpoint).Port;
        Task socksTask = RunFakeSocks5ServerAsync(socksListener, "api.anthropic.com", 443, cancellationToken);

        var command = DotnetTunDemoCommand.Parse(
            [
                "bridge",
                "--listen", "127.0.0.1:0",
                "--fake-ip", "198.18.0.1",
                "--domain", "api.anthropic.com",
                "--target-port", "443",
                "--socks5", $"127.0.0.1:{socksPort}"
            ]);

        // Act
        await using DotnetTunDemoCommandHandle handle = await command.StartAsync(cancellationToken);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, handle.Port, cancellationToken);
        await using NetworkStream stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 0x42 }, cancellationToken);
        byte[] response = new byte[1];
        int bytesRead = await stream.ReadAsync(response, cancellationToken);
        client.Close();
        await socksTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);

        // Assert
        Assert.Equal(1, bytesRead);
        Assert.Equal(0x24, response[0]);
    }

    private static async Task RunFakeSocks5ServerAsync(TcpListener listener, string expectedHost, int expectedPort, CancellationToken cancellationToken)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = client.GetStream();

        byte[] greeting = await ReadExactAsync(stream, 3, cancellationToken);
        Assert.Equal([0x05, 0x01, 0x00], greeting);
        await stream.WriteAsync(new byte[] { 0x05, 0x00 }, cancellationToken);

        byte[] requestPrefix = await ReadExactAsync(stream, 5, cancellationToken);
        Assert.Equal(0x05, requestPrefix[0]);
        Assert.Equal(0x01, requestPrefix[1]);
        Assert.Equal(0x00, requestPrefix[2]);
        Assert.Equal(0x03, requestPrefix[3]);

        int hostLength = requestPrefix[4];
        byte[] hostBytes = await ReadExactAsync(stream, hostLength, cancellationToken);
        byte[] portBytes = await ReadExactAsync(stream, 2, cancellationToken);
        string host = System.Text.Encoding.ASCII.GetString(hostBytes);
        int port = (portBytes[0] << 8) | portBytes[1];
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);

        await stream.WriteAsync(new byte[] { 0x05, 0x00, 0x00, 0x01, 127, 0, 0, 1, 0x1F, 0x90 }, cancellationToken);

        byte[] payload = await ReadExactAsync(stream, 1, cancellationToken);
        Assert.Equal(0x42, payload[0]);
        await stream.WriteAsync(new byte[] { 0x24 }, cancellationToken);
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        return buffer;
    }
}
