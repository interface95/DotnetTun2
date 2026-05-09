using System.Net;
using DotnetTun.Platforms.MacOS.Networking;
using Xunit;

namespace DotnetTun.Platforms.MacOS.Tests.Networking;

public sealed class MacRouteCommandBuilderTests
{
    [Fact]
    public void BuildConfigureCommands_IncludesIfconfigForUtunAddressGatewayAndMtu()
    {
        // Arrange
        var options = CreateOptions();
        var builder = new MacRouteCommandBuilder();

        // Act
        string[] commands = builder.BuildConfigureCommands(options);

        // Assert
        Assert.Contains("sudo ifconfig utun9 10.88.0.2 10.88.0.1 netmask 255.255.255.255 up", commands);
        Assert.Contains("sudo ifconfig utun9 mtu 1420", commands);
    }

    [Fact]
    public void BuildConfigureCommands_IncludesRouteForFakeIpCidrViaUtun()
    {
        // Arrange
        var options = CreateOptions();
        var builder = new MacRouteCommandBuilder();

        // Act
        string[] commands = builder.BuildConfigureCommands(options);

        // Assert
        Assert.Contains("sudo route add -net 198.18.0.0/15 -interface utun9", commands);
    }

    [Fact]
    public void BuildExcludeCommands_CreatesHostRoutesThroughDefaultGateway()
    {
        // Arrange
        var options = CreateOptions();
        var builder = new MacRouteCommandBuilder();

        // Act
        string[] commands = builder.BuildExcludeCommands(options, IPAddress.Parse("192.168.1.1"));

        // Assert
        Assert.Contains("sudo route add -host 1.1.1.1 192.168.1.1", commands);
        Assert.Contains("sudo route add -host 203.0.113.10 192.168.1.1", commands);
    }

    private static MacTunOptions CreateOptions()
        => new(
            InterfaceName: "utun9",
            Address: IPAddress.Parse("10.88.0.2"),
            Gateway: IPAddress.Parse("10.88.0.1"),
            Mtu: 1420,
            FakeIpCidr: "198.18.0.0/15",
            ExcludedIps: [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("203.0.113.10")]);
}
