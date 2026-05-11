namespace DotnetTun.Outbounds.Socks5;

public sealed record Socks5OutboundOptions
{
    private static readonly TimeSpan DefaultHandshakeTimeout = TimeSpan.FromSeconds(10);

    public Socks5OutboundOptions(string host, int port, TimeSpan? HandshakeTimeout = null, string name = "socks5")
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
        Name = string.IsNullOrWhiteSpace(name)
            ? throw new ArgumentException("SOCKS5 outbound name must not be empty.", nameof(name))
            : name.Trim();
        this.HandshakeTimeout = ValidateHandshakeTimeout(HandshakeTimeout ?? DefaultHandshakeTimeout);
    }

    public string Name { get; }

    public string Host { get; }

    public int Port { get; }

    public TimeSpan HandshakeTimeout { get; }

    public override string ToString() => $"socks5://{Host}:{Port}";

    private static TimeSpan ValidateHandshakeTimeout(TimeSpan handshakeTimeout)
    {
        if (handshakeTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(handshakeTimeout), "SOCKS5 handshake timeout must be greater than zero.");
        }

        return handshakeTimeout;
    }
}
