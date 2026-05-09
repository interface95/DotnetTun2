using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using DotnetTun.Abstractions.Dns;

namespace DotnetTun.Core.Dns;

public sealed class FakeIpPool
{
    private readonly uint _start;
    private readonly uint _end;
    private uint _next;
    private readonly Dictionary<string, FakeIpLease> _leasesByDomain = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IPAddress, string> _domainsByFakeIp = new();

    public FakeIpPool()
        : this(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.19.255.254"))
    {
    }

    public FakeIpPool(IPAddress start, IPAddress end)
    {
        _start = ToUInt32(start);
        _end = ToUInt32(end);

        if (_start > _end)
        {
            throw new ArgumentException("Fake-IP range start must be less than or equal to end.", nameof(start));
        }

        _next = _start;
    }

    public FakeIpLease Allocate(string domain)
    {
        string normalizedDomain = NormalizeDomain(domain);
        if (_leasesByDomain.TryGetValue(normalizedDomain, out FakeIpLease? existing))
        {
            return existing;
        }

        if (_next > _end)
        {
            throw new InvalidOperationException("Fake-IP pool is exhausted.");
        }

        IPAddress fakeIp = FromUInt32(_next++);
        var lease = new FakeIpLease(normalizedDomain, fakeIp);

        _leasesByDomain.Add(normalizedDomain, lease);
        _domainsByFakeIp.Add(fakeIp, normalizedDomain);

        return lease;
    }

    public bool TryResolve(IPAddress fakeIp, [NotNullWhen(true)] out string? domain)
        => _domainsByFakeIp.TryGetValue(fakeIp, out domain);

    private static string NormalizeDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            throw new ArgumentException("Domain must not be empty.", nameof(domain));
        }

        return domain.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static uint ToUInt32(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Fake-IP pool supports IPv4 addresses only.", nameof(address));
        }

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
