using System.Net;

namespace DotnetTun.Platforms.MacOS.Networking;

public sealed record MacTunOptions
{
    public MacTunOptions(string InterfaceName, IPAddress Address, IPAddress Gateway, int Mtu, string FakeIpCidr, IReadOnlyList<IPAddress> ExcludedIps)
        : this(InterfaceName, Address, Gateway, Mtu, FakeIpCidr, ExcludedIps, DefaultGateway: null)
    {
    }

    public MacTunOptions(
        string InterfaceName,
        IPAddress Address,
        IPAddress Gateway,
        int Mtu,
        string FakeIpCidr,
        IReadOnlyList<IPAddress> ExcludedIps,
        IPAddress? DefaultGateway = null)
    {
        if (string.IsNullOrWhiteSpace(InterfaceName))
        {
            throw new ArgumentException("Interface name must not be empty.", nameof(InterfaceName));
        }

        string interfaceName = ValidateInterfaceName(InterfaceName);

        if (Mtu <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Mtu), "MTU must be positive.");
        }

        string fakeIpCidr = ValidateCidr(FakeIpCidr);

        this.InterfaceName = interfaceName;
        this.Address = Address;
        this.Gateway = Gateway;
        this.Mtu = Mtu;
        this.FakeIpCidr = fakeIpCidr;
        this.ExcludedIps = ExcludedIps;
        this.DefaultGateway = DefaultGateway;
    }

    public string InterfaceName { get; }

    public IPAddress Address { get; }

    public IPAddress Gateway { get; }

    public int Mtu { get; }

    public string FakeIpCidr { get; }

    public IReadOnlyList<IPAddress> ExcludedIps { get; }

    public IPAddress? DefaultGateway { get; }

    private static string ValidateInterfaceName(string interfaceName)
    {
        string trimmed = interfaceName.Trim();
        if (trimmed == "utun-auto")
        {
            return trimmed;
        }

        if (trimmed.Length <= "utun".Length || !trimmed.StartsWith("utun", StringComparison.Ordinal))
        {
            throw new ArgumentException("Interface name must be a macOS utun interface such as utun9.", nameof(interfaceName));
        }

        ReadOnlySpan<char> unit = trimmed.AsSpan("utun".Length);
        foreach (char character in unit)
        {
            if (!char.IsAsciiDigit(character))
            {
                throw new ArgumentException("Interface name must be a macOS utun interface such as utun9.", nameof(interfaceName));
            }
        }

        return trimmed;
    }

    private static string ValidateCidr(string fakeIpCidr)
    {
        if (string.IsNullOrWhiteSpace(fakeIpCidr))
        {
            throw new ArgumentException("Fake-IP CIDR must not be empty.", nameof(fakeIpCidr));
        }

        string trimmed = fakeIpCidr.Trim();
        string[] parts = trimmed.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out IPAddress? address) || !int.TryParse(parts[1], out int prefixLength))
        {
            throw new ArgumentException("Fake-IP CIDR must be a valid CIDR block.", nameof(fakeIpCidr));
        }

        int maxPrefixLength = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maxPrefixLength)
        {
            throw new ArgumentException("Fake-IP CIDR prefix length is out of range.", nameof(fakeIpCidr));
        }

        return trimmed;
    }
}
