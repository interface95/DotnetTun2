using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using DotnetTun.Abstractions.Dns;

namespace DotnetTun.Core.Dns;

public sealed class FakeIpStore : IFakeIpStore
{
    private readonly uint _start;
    private readonly uint _end;
    private uint _next;
    private readonly Dictionary<string, IPAddress> _addressByDomain = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IPAddress, string> _domainByAddress = new();
    private readonly object _gate = new();

    public FakeIpStore()
        : this(IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.19.255.254"))
    {
    }

    public FakeIpStore(IPAddress start, IPAddress end)
    {
        _start = ToUInt32(start);
        _end = ToUInt32(end);

        if (_start > _end)
        {
            throw new ArgumentException("Fake-IP range start must be less than or equal to end.", nameof(start));
        }

        _next = _start;
    }

    public IPAddress Allocate(string domain)
    {
        var normalizedDomain = NormalizeDomain(domain);
        lock (_gate)
        {
            if (_addressByDomain.TryGetValue(normalizedDomain, out var existing))
            {
                return existing;
            }

            if (_next > _end)
            {
                throw new InvalidOperationException("Fake-IP store is exhausted.");
            }

            var address = FromUInt32(_next++);
            _addressByDomain.Add(normalizedDomain, address);
            _domainByAddress.Add(address, normalizedDomain);
            return address;
        }
    }

    public bool TryResolve(IPAddress fakeIp, [NotNullWhen(true)] out string? domain)
    {
        lock (_gate)
        {
            return _domainByAddress.TryGetValue(fakeIp, out domain);
        }
    }

    private static string NormalizeDomain(string domain)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(domain);
        return domain.Trim().TrimEnd('.').ToLowerInvariant();
    }

    private static uint ToUInt32(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException("Fake-IP store supports IPv4 addresses only.", nameof(address));
        }

        var bytes = address.MapToIPv4().GetAddressBytes();
        return BinaryPrimitives.ReadUInt32BigEndian(bytes);
    }

    private static IPAddress FromUInt32(uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
