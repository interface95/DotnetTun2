using System.Net;
using DotnetTun.Platforms.MacOS.Networking;
using Xunit;

namespace DotnetTun.Platforms.MacOS.Tests.Networking;

public sealed class MacTunConfiguratorTests
{
    [Fact]
    public async Task ConfigureAsync_RunsConfigureCommandsInOrder()
    {
        // Arrange
        var runner = new RecordingCommandRunner();
        var configurator = new MacTunConfigurator(new MacRouteCommandBuilder(), runner);
        var options = CreateOptions();

        // Act
        await configurator.ConfigureAsync(options, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(
            new MacRouteCommandBuilder().BuildConfigureCommands(options),
            runner.Commands);
    }

    [Fact]
    public async Task ConfigureAsync_WhenDefaultGatewayIsKnown_RunsExcludeCommandsAfterConfigureCommands()
    {
        // Arrange
        var runner = new RecordingCommandRunner();
        var configurator = new MacTunConfigurator(new MacRouteCommandBuilder(), runner);
        var options = CreateOptions(
            ExcludedIps: [IPAddress.Parse("203.0.113.10")],
            DefaultGateway: IPAddress.Parse("192.168.1.1"));
        MacCommand[] expectedCommands =
        [
            .. new MacRouteCommandBuilder().BuildConfigureCommands(options),
            .. new MacRouteCommandBuilder().BuildExcludeCommands(options, IPAddress.Parse("192.168.1.1"))
        ];

        // Act
        await configurator.ConfigureAsync(options, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedCommands, runner.Commands);
    }

    [Fact]
    public async Task CleanupAsync_RunsCleanupCommandsInOrder()
    {
        // Arrange
        var runner = new RecordingCommandRunner();
        var configurator = new MacTunConfigurator(new MacRouteCommandBuilder(), runner);
        var options = CreateOptions();

        // Act
        await configurator.CleanupAsync(options, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(
            new MacRouteCommandBuilder().BuildCleanupCommands(options),
            runner.Commands);
    }

    [Fact]
    public async Task CleanupAsync_WhenExcludedIpsExist_RemovesExcludedHostRoutesBeforeTunRoutes()
    {
        // Arrange
        var runner = new RecordingCommandRunner();
        var configurator = new MacTunConfigurator(new MacRouteCommandBuilder(), runner);
        var options = CreateOptions(ExcludedIps: [IPAddress.Parse("203.0.113.10")]);
        MacCommand[] expectedCommands =
        [
            .. new MacRouteCommandBuilder().BuildExcludeCleanupCommands(options),
            .. new MacRouteCommandBuilder().BuildCleanupCommands(options)
        ];

        // Act
        await configurator.CleanupAsync(options, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expectedCommands, runner.Commands);
    }

    [Fact]
    public async Task ConfigureAsync_UsesStructuredCommandsWithoutShellOperators()
    {
        // Arrange
        var runner = new RecordingCommandRunner();
        var configurator = new MacTunConfigurator(new MacRouteCommandBuilder(), runner);
        var options = CreateOptions();

        // Act
        await configurator.ConfigureAsync(options, TestContext.Current.CancellationToken);

        // Assert
        Assert.All(runner.Commands, command =>
        {
            Assert.Equal("sudo", command.Executable);
            Assert.DoesNotContain("||", command.Executable, StringComparison.Ordinal);
            Assert.DoesNotContain("2>", command.Executable, StringComparison.Ordinal);
            Assert.DoesNotContain(";", command.Executable, StringComparison.Ordinal);
            Assert.DoesNotContain("-lc", command.Arguments);
            Assert.All(command.Arguments, argument =>
            {
                Assert.DoesNotContain("||", argument, StringComparison.Ordinal);
                Assert.DoesNotContain("2>", argument, StringComparison.Ordinal);
                Assert.DoesNotContain(";", argument, StringComparison.Ordinal);
            });
        });
    }

    [Fact]
    public async Task CleanupAsync_MarksDeleteCommandsAsIgnoreFailureWithoutShellOperators()
    {
        // Arrange
        var runner = new RecordingCommandRunner();
        var configurator = new MacTunConfigurator(new MacRouteCommandBuilder(), runner);
        var options = CreateOptions();

        // Act
        await configurator.CleanupAsync(options, TestContext.Current.CancellationToken);

        // Assert
        Assert.All(runner.Commands, command =>
        {
            Assert.True(command.IgnoreFailure);
            Assert.DoesNotContain("2>/dev/null || true", command.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task CleanupAsync_WhenDeleteCommandFailsAndIgnoreFailureIsSet_CompletesCleanup()
    {
        // Arrange
        var runner = new IgnoringFailureCommandRunner();
        var configurator = new MacTunConfigurator(new MacRouteCommandBuilder(), runner);
        var options = CreateOptions();

        // Act
        await configurator.CleanupAsync(options, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, runner.Commands.Count);
    }

    private static MacTunOptions CreateOptions(IReadOnlyList<IPAddress>? ExcludedIps = null, IPAddress? DefaultGateway = null)
        => new(
            InterfaceName: "utun9",
            Address: IPAddress.Parse("10.88.0.2"),
            Gateway: IPAddress.Parse("10.88.0.1"),
            Mtu: 1420,
            FakeIpCidr: "198.18.0.0/15",
            ExcludedIps: ExcludedIps ?? [],
            DefaultGateway: DefaultGateway);

    private sealed class RecordingCommandRunner : IMacCommandRunner
    {
        public List<MacCommand> Commands { get; } = [];

        public ValueTask RunAsync(MacCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IgnoringFailureCommandRunner : IMacCommandRunner
    {
        public List<MacCommand> Commands { get; } = [];

        public ValueTask RunAsync(MacCommand command, CancellationToken cancellationToken = default)
        {
            Commands.Add(command);
            if (!command.IgnoreFailure)
            {
                throw new InvalidOperationException("cleanup delete command was not marked ignore-failure");
            }

            return ValueTask.CompletedTask;
        }
    }
}
