namespace DotnetTun.Outbounds.Socks5;

public sealed record Socks5OutboundOptions
{
    public Socks5OutboundOptions(string host, int port)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new ArgumentException("SOCKS5 host must not be empty.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "SOCKS5 port must be between 1 and 65535.");
        }

        Host = host.Trim();
        Port = port;
    }

    public string Host { get; }

    public int Port { get; }

    public override string ToString() => $"socks5://{Host}:{Port}";
}
