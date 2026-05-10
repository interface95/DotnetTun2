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
        MacCommand[] commands = builder.BuildConfigureCommands(options);

        // Assert
        Assert.Contains(commands, command =>
            command.Executable == "sudo"
            && command.Arguments.SequenceEqual(["ifconfig", "utun9", "10.88.0.2", "10.88.0.1", "netmask", "255.255.255.255", "up"]));
        Assert.Contains(commands, command =>
            command.Executable == "sudo"
            && command.Arguments.SequenceEqual(["ifconfig", "utun9", "mtu", "1420"]));
    }

    [Fact]
    public void BuildConfigureCommands_IncludesRouteForFakeIpCidrViaUtun()
    {
        // Arrange
        var options = CreateOptions();
        var builder = new MacRouteCommandBuilder();

        // Act
        MacCommand[] commands = builder.BuildConfigureCommands(options);

        // Assert
        Assert.Contains(commands, command =>
            command.Executable == "sudo"
            && command.Arguments.SequenceEqual(["route", "add", "-net", "198.18.0.0/15", "-interface", "utun9"]));
    }

    [Fact]
    public void BuildExcludeCommands_CreatesHostRoutesThroughDefaultGateway()
    {
        // Arrange
        var options = CreateOptions();
        var builder = new MacRouteCommandBuilder();

        // Act
        MacCommand[] commands = builder.BuildExcludeCommands(options, IPAddress.Parse("192.168.1.1"));

        // Assert
        Assert.Contains(commands, command =>
            command.Executable == "sudo"
            && command.Arguments.SequenceEqual(["route", "add", "-host", "1.1.1.1", "192.168.1.1"]));
        Assert.Contains(commands, command =>
            command.Executable == "sudo"
            && command.Arguments.SequenceEqual(["route", "add", "-host", "203.0.113.10", "192.168.1.1"]));
    }

    [Fact]
    public void BuildCleanupCommands_RemovesFakeIpAndTunHostRoutes()
    {
        // Arrange
        var options = CreateOptions();
        var builder = new MacRouteCommandBuilder();

        // Act
        MacCommand[] commands = builder.BuildCleanupCommands(options);

        // Assert
        Assert.Contains(commands, command =>
            command.Executable == "sudo"
            && command.IgnoreFailure
            && command.Arguments.SequenceEqual(["route", "delete", "-net", "198.18.0.0/15"]));
        Assert.Contains(commands, command =>
            command.Executable == "sudo"
            && command.IgnoreFailure
            && command.Arguments.SequenceEqual(["route", "delete", "-host", "10.88.0.2"]));
        Assert.Contains(commands, command =>
            command.Executable == "sudo"
            && command.IgnoreFailure
            && command.Arguments.SequenceEqual(["route", "delete", "-host", "10.88.0.1"]));
        Assert.All(commands, command => Assert.DoesNotContain("2>/dev/null || true", command.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void BuildExcludeCleanupCommands_RemovesExcludedHostRoutesWithIgnoreFailure()
    {
        // Arrange
        var options = CreateOptions();
        var builder = new MacRouteCommandBuilder();

        // Act
        MacCommand[] commands = builder.BuildExcludeCleanupCommands(options);

        // Assert
        Assert.Contains(commands, command =>
            command.Executable == "sudo"
            && command.IgnoreFailure
            && command.Arguments.SequenceEqual(["route", "delete", "-host", "1.1.1.1"]));
        Assert.Contains(commands, command =>
            command.Executable == "sudo"
            && command.IgnoreFailure
            && command.Arguments.SequenceEqual(["route", "delete", "-host", "203.0.113.10"]));
        Assert.All(commands, command => Assert.DoesNotContain("2>/dev/null || true", command.ToString(), StringComparison.Ordinal));
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
