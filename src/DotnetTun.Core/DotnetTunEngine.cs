using System.Buffers.Binary;
using System.Net;
using DotnetTun.Abstractions;
using DotnetTun.Abstractions.Dns;
using DotnetTun.Core.Dns;

namespace DotnetTun.Core;

public sealed class DotnetTunEngine
{
    public DotnetTunDryRunPlan CreateDryRun(DotnetTunOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        (IPAddress start, IPAddress end) = ParseIpv4Cidr(options.FakeIpCidr);
        var pool = new FakeIpPool(start, end);
        List<FakeIpLease> exactLeases = [];
        List<string> wildcardPatterns = [];

        foreach (string domain in options.InterceptDomains)
        {
            string normalized = NormalizeDomain(domain);
            if (normalized.StartsWith("*.", StringComparison.Ordinal))
            {
                wildcardPatterns.Add(normalized);
                continue;
            }

            exactLeases.Add(pool.Allocate(normalized));
        }

        return new DotnetTunDryRunPlan(exactLeases, wildcardPatterns);
    }

    private static string NormalizeDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new ArgumentException("Intercept domain must not be empty.", nameof(domain));
        }

        return domain.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static (IPAddress start, IPAddress end) ParseIpv4Cidr(string cidr)
    {
        string[] parts = cidr.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out IPAddress? address) || !int.TryParse(parts[1], out int prefixLength))
        {
            throw new ArgumentException("Fake-IP CIDR must use IPv4 CIDR notation, for example 198.18.0.0/15.", nameof(cidr));
        }

        if (prefixLength is < 0 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(cidr), "IPv4 CIDR prefix length must be between 0 and 32.");
        }

        uint ip = ToUInt32(address);
        uint mask = prefixLength == 0 ? 0U : uint.MaxValue << (32 - prefixLength);
        uint start = ip & mask;
        uint end = start | ~mask;

        if (start < end)
        {
            start++;
            end--;
        }

        return (FromUInt32(start), FromUInt32(end));
    }

    private static uint ToUInt32(IPAddress address)
    {
        byte[] bytes = address.MapToIPv4().GetAddressBytes();
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static IPAddress FromUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
