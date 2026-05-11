namespace DotnetTun.Abstractions.Routing;

public sealed record ConnectionContext
{
    public ConnectionContext(string host, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 0 and 65535.");
        }

        Host = host.Trim().TrimEnd('.').ToLowerInvariant();
        Port = port;
    }

    public string Host { get; }

    public int Port { get; }
}
