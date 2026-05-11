using System.Net;
using System.Net.Sockets;
using System.Text;
using DotnetTun.Abstractions;

namespace DotnetTun.Outbounds.Socks5;

public sealed class Socks5Outbound(Socks5OutboundOptions options) : IOutbound
{
    public string Name => options.Name;

    public async ValueTask<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("Target host must not be empty.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Target port must be between 1 and 65535.");
        }

        var client = new TcpClient();
        try
        {
            using var handshakeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeCancellation.CancelAfter(options.HandshakeTimeout);

            try
            {
                await client.ConnectAsync(options.Host, options.Port, handshakeCancellation.Token).ConfigureAwait(false);
                var stream = client.GetStream();

                await WriteGreetingAsync(stream, handshakeCancellation.Token).ConfigureAwait(false);
                await ReadGreetingResponseAsync(stream, handshakeCancellation.Token).ConfigureAwait(false);
                await WriteConnectRequestAsync(stream, host.Trim(), port, handshakeCancellation.Token).ConfigureAwait(false);
                await ReadConnectResponseAsync(stream, handshakeCancellation.Token).ConfigureAwait(false);

                return stream;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && handshakeCancellation.IsCancellationRequested)
            {
                throw new TimeoutException($"SOCKS5 handshake timed out after {options.HandshakeTimeout}.");
            }
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task WriteGreetingAsync(Stream stream, CancellationToken cancellationToken)
        => await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken).ConfigureAwait(false);

    private static async Task ReadGreetingResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] response = new byte[2];
        await stream.ReadExactlyAsync(response, cancellationToken).ConfigureAwait(false);

        if (response is not [0x05, 0x00])
        {
            throw new InvalidOperationException("SOCKS5 server did not accept no-auth authentication.");
        }
    }

    private static async Task WriteConnectRequestAsync(Stream stream, string host, int port, CancellationToken cancellationToken)
    {
        byte[] addressBytes = CreateAddressBytes(host, out byte addressType);
        byte[] request = new byte[4 + addressBytes.Length + 2];
        request[0] = 0x05;
        request[1] = 0x01;
        request[2] = 0x00;
        request[3] = addressType;
        addressBytes.CopyTo(request.AsSpan(4));
        request[^2] = (byte)(port >> 8);
        request[^1] = (byte)(port & 0xFF);

        await stream.WriteAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static byte[] CreateAddressBytes(string host, out byte addressType)
    {
        if (IPAddress.TryParse(host, out IPAddress? ipAddress))
        {
            byte[] bytes = ipAddress.GetAddressBytes();
            addressType = ipAddress.AddressFamily switch
            {
                AddressFamily.InterNetwork => 0x01,
                AddressFamily.InterNetworkV6 => 0x04,
                _ => throw new ArgumentException("Target host must be an IPv4 address, IPv6 address, or DNS name.", nameof(host))
            };
            return bytes;
        }

        byte[] domainBytes = Encoding.ASCII.GetBytes(host);
        if (domainBytes.Length > 255)
        {
            throw new ArgumentException("SOCKS5 domain target must be 255 bytes or fewer.", nameof(host));
        }

        addressType = 0x03;
        byte[] result = new byte[domainBytes.Length + 1];
        result[0] = (byte)domainBytes.Length;
        domainBytes.CopyTo(result.AsSpan(1));
        return result;
    }

    private static async Task ReadConnectResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var prefix = new byte[4];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);

        if (prefix[0] != 0x05)
        {
            throw new InvalidOperationException("SOCKS5 server returned an invalid response version.");
        }

        if (prefix[1] != 0x00)
        {
            throw new InvalidOperationException($"SOCKS5 CONNECT failed with reply code 0x{prefix[1]:X2}.");
        }

        int addressLength = prefix[3] switch
        {
            0x01 => 4,
            0x03 => await ReadDomainLengthAsync(stream, cancellationToken).ConfigureAwait(false),
            0x04 => 16,
            _ => throw new InvalidOperationException("SOCKS5 server returned an invalid bound address type.")
        };

        byte[] remaining = new byte[addressLength + 2];
        await stream.ReadExactlyAsync(remaining, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadDomainLengthAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] length = new byte[1];
        await stream.ReadExactlyAsync(length, cancellationToken).ConfigureAwait(false);
        return length[0];
    }
}
