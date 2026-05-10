using System.Net;

namespace DotnetTun.Platforms.MacOS.Networking;

public sealed class MacRouteCommandBuilder
{
    public MacCommand[] BuildConfigureCommands(MacTunOptions options)
        =>
        [
            new("sudo", ["ifconfig", options.InterfaceName, options.Address.ToString(), options.Gateway.ToString(), "netmask", "255.255.255.255", "up"]),
            new("sudo", ["ifconfig", options.InterfaceName, "mtu", options.Mtu.ToString(System.Globalization.CultureInfo.InvariantCulture)]),
            new("sudo", ["sysctl", "-w", "net.inet.ip.forwarding=1"]),
            new("sudo", ["sysctl", "-w", "net.inet.ip.redirect=0"]),
            new("sudo", ["route", "delete", "-net", options.FakeIpCidr], IgnoreFailure: true),
            new("sudo", ["route", "add", "-net", options.FakeIpCidr, "-interface", options.InterfaceName]),
            new("sudo", ["route", "add", "-host", options.Address.ToString(), "-interface", options.InterfaceName]),
            new("sudo", ["route", "add", "-host", options.Gateway.ToString(), "-interface", options.InterfaceName])
        ];

    public MacCommand[] BuildExcludeCommands(MacTunOptions options, IPAddress defaultGateway)
        => [.. options.ExcludedIps.Select(ip => new MacCommand("sudo", ["route", "add", "-host", ip.ToString(), defaultGateway.ToString()]))];

    public MacCommand[] BuildExcludeCleanupCommands(MacTunOptions options)
        => [.. options.ExcludedIps.Select(ip => new MacCommand("sudo", ["route", "delete", "-host", ip.ToString()], IgnoreFailure: true))];

    public MacCommand[] BuildCleanupCommands(MacTunOptions options)
        =>
        [
            new("sudo", ["route", "delete", "-net", options.FakeIpCidr], IgnoreFailure: true),
            new("sudo", ["route", "delete", "-host", options.Address.ToString()], IgnoreFailure: true),
            new("sudo", ["route", "delete", "-host", options.Gateway.ToString()], IgnoreFailure: true),
        ];
}
