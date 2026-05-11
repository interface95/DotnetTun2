namespace DotnetTun.Abstractions.Dns;

public enum DnsHandlingDisposition
{
    Intercepted,
    Forwarded,
    Dropped,
}

public sealed record DnsHandlingResult
{
    public required DnsHandlingDisposition Disposition { get; init; }

    public byte[]? Response { get; init; }

    public static DnsHandlingResult Intercepted(byte[] response)
        => new() { Disposition = DnsHandlingDisposition.Intercepted, Response = response ?? throw new ArgumentNullException(nameof(response)) };

    public static DnsHandlingResult Forwarded(byte[] response)
        => new() { Disposition = DnsHandlingDisposition.Forwarded, Response = response ?? throw new ArgumentNullException(nameof(response)) };

    public static DnsHandlingResult Dropped() => new() { Disposition = DnsHandlingDisposition.Dropped };
}
