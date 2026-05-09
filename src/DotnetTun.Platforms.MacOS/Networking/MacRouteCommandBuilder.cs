using System.Net;

namespace DotnetTun.Platforms.MacOS.Networking;

public sealed class MacRouteCommandBuilder
{
    public string[] BuildConfigureCommands(MacTunOptions options)
        =>
        [
            $"sudo ifconfig {options.InterfaceName} {options.Address} {options.Gateway} netmask 255.255.255.255 up",
            $"sudo ifconfig {options.InterfaceName} mtu {options.Mtu}",
            "sudo sysctl -w net.inet.ip.forwarding=1",
            "sudo sysctl -w net.inet.ip.redirect=0",
            $"sudo route delete -net {options.FakeIpCidr} 2>/dev/null || true",
            $"sudo route add -net {options.FakeIpCidr} -interface {options.InterfaceName}",
            $"sudo route add -host {options.Address} -interface {options.InterfaceName}",
            $"sudo route add -host {options.Gateway} -interface {options.InterfaceName}"
        ];

    public string[] BuildExcludeCommands(MacTunOptions options, IPAddress defaultGateway)
        => [.. options.ExcludedIps.Select(ip => $"sudo route add -host {ip} {defaultGateway}")];
}
