using System.Net;

namespace DotnetTun.Platforms.MacOS.Networking;

public sealed record MacTunOptions
{
    public MacTunOptions(string InterfaceName, IPAddress Address, IPAddress Gateway, int Mtu, string FakeIpCidr, IReadOnlyList<IPAddress> ExcludedIps)
    {
        if (string.IsNullOrWhiteSpace(InterfaceName))
        {
            throw new ArgumentException("Interface name must not be empty.", nameof(InterfaceName));
        }

        if (Mtu <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Mtu), "MTU must be positive.");
        }

        if (string.IsNullOrWhiteSpace(FakeIpCidr))
        {
            throw new ArgumentException("Fake-IP CIDR must not be empty.", nameof(FakeIpCidr));
        }

        this.InterfaceName = InterfaceName.Trim();
        this.Address = Address;
        this.Gateway = Gateway;
        this.Mtu = Mtu;
        this.FakeIpCidr = FakeIpCidr.Trim();
        this.ExcludedIps = ExcludedIps;
    }

    public string InterfaceName { get; }

    public IPAddress Address { get; }

    public IPAddress Gateway { get; }

    public int Mtu { get; }

    public string FakeIpCidr { get; }

    public IReadOnlyList<IPAddress> ExcludedIps { get; }
}
