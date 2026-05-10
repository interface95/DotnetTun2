using System.Net;
using DotnetTun.Abstractions;
using DotnetTun.Demo.Cli;
using DotnetTun.Platforms.MacOS.Networking;
using Xunit;

namespace DotnetTun.Demo.Cli.Tests;

public sealed class TunCommandTests
{
    [Fact]
    public async Task RunAsync_WithTunDryRun_PrintsRawTunPlan()
    {
        // Arrange
        var writer = new StringWriter();
        TextWriter originalOut = Console.Out;
        Console.SetOut(writer);
        try
        {
            var command = DotnetTunDemoCommand.Parse(
                [
                    "tun",
                    "--dry-run",
                    "--fake-ip", "198.18.0.1",
                    "--domain", "api.anthropic.com",
                    "--socks5", "127.0.0.1:7890"
                ]);

            // Act
            int exitCode = await command.RunAsync(TestContext.Current.CancellationToken);

            // Assert
            string output = writer.ToString();
            Assert.Equal(0, exitCode);
            Assert.Contains("DotnetTun raw TUN plan", output, StringComparison.Ordinal);
            Assert.Contains("198.18.0.1 -> api.anthropic.com", output, StringComparison.Ordinal);
            Assert.Contains("socks5://127.0.0.1:7890", output, StringComparison.Ordinal);
            Assert.Contains("macOS configure commands", output, StringComparison.Ordinal);
            Assert.Contains("sudo route add -net 198.18.0.1/32 -interface utun-auto", output, StringComparison.Ordinal);
            Assert.Contains("macOS cleanup commands", output, StringComparison.Ordinal);
            Assert.Contains("sudo route delete -net 198.18.0.1/32", output, StringComparison.Ordinal);
            Assert.DoesNotContain("2>/dev/null || true", output, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public async Task RunAsync_WithTunRealRun_OpensUtunBeforeConfigureAndUsesActualInterfaceName()
    {
        // Arrange
        var operations = new List<string>();
        var device = new RecordingTunDevice(operations, interfaceName: "utun9");
        var commandRunner = new RecordingMacCommandRunner(operations);
        var proxy = new RecordingTunProxy(operations);
        var runtime = new TunDemoRuntime(
            CreateTunDevice: () => device,
            CreateConfigurator: () => new MacTunConfigurator(new MacRouteCommandBuilder(), commandRunner),
            CreateRawTunProxy: (_, _, _, _, _) => proxy);
        DotnetTunDemoCommand command = DotnetTunDemoCommand.Parse(
            [
                "tun",
                "--fake-ip", "198.18.0.1",
                "--domain", "api.anthropic.com",
                "--socks5", "127.0.0.1:7890"
            ],
            runtime);

        // Act
        int exitCode = await command.RunAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Equal("open:utun9", operations[0]);
        Assert.Contains(operations, operation => operation == "run:123");
        Assert.Equal("close:123", operations[^1]);

        int firstConfigureIndex = operations.FindIndex(operation => operation.StartsWith("command:sudo ifconfig", StringComparison.Ordinal));
        int runIndex = operations.IndexOf("run:123");
        int firstCleanupIndex = operations.FindLastIndex(operation => operation.StartsWith("command:sudo route delete", StringComparison.Ordinal));
        int closeIndex = operations.IndexOf("close:123");
        Assert.True(firstConfigureIndex > 0);
        Assert.True(firstConfigureIndex < runIndex);
        Assert.True(runIndex < firstCleanupIndex);
        Assert.True(firstCleanupIndex < closeIndex);
        Assert.Contains("utun9", operations[firstConfigureIndex], StringComparison.Ordinal);
        Assert.DoesNotContain(operations, operation => operation.Contains("utun-auto", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_WithRemoteSocks5Server_AddsAndCleansExcludeRouteThroughDefaultGateway()
    {
        // Arrange
        var operations = new List<string>();
        var device = new RecordingTunDevice(operations, interfaceName: "utun9");
        var commandRunner = new RecordingMacCommandRunner(operations);
        var proxy = new RecordingTunProxy(operations);
        var runtime = new TunDemoRuntime(
            CreateTunDevice: () => device,
            CreateConfigurator: () => new MacTunConfigurator(new MacRouteCommandBuilder(), commandRunner),
            CreateRawTunProxy: (_, _, _, _, _) => proxy,
            GetDefaultGatewayAsync: _ => ValueTask.FromResult<IPAddress?>(IPAddress.Parse("192.168.1.1")));
        DotnetTunDemoCommand command = DotnetTunDemoCommand.Parse(
            [
                "tun",
                "--fake-ip", "198.18.0.1",
                "--domain", "api.anthropic.com",
                "--socks5", "203.0.113.10:7890"
            ],
            runtime);

        // Act
        int exitCode = await command.RunAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Contains("command:sudo route add -host 203.0.113.10 192.168.1.1", operations);
        Assert.Contains("command:sudo route delete -host 203.0.113.10", operations);
    }

    [Fact]
    public async Task RunAsync_WithLoopbackSocks5Server_DoesNotAddExcludeRoute()
    {
        // Arrange
        var operations = new List<string>();
        var device = new RecordingTunDevice(operations, interfaceName: "utun9");
        var commandRunner = new RecordingMacCommandRunner(operations);
        var proxy = new RecordingTunProxy(operations);
        var runtime = new TunDemoRuntime(
            CreateTunDevice: () => device,
            CreateConfigurator: () => new MacTunConfigurator(new MacRouteCommandBuilder(), commandRunner),
            CreateRawTunProxy: (_, _, _, _, _) => proxy,
            GetDefaultGatewayAsync: _ => ValueTask.FromResult<IPAddress?>(IPAddress.Parse("192.168.1.1")));
        DotnetTunDemoCommand command = DotnetTunDemoCommand.Parse(
            [
                "tun",
                "--fake-ip", "198.18.0.1",
                "--domain", "api.anthropic.com",
                "--socks5", "127.0.0.1:7890"
            ],
            runtime);

        // Act
        int exitCode = await command.RunAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, exitCode);
        Assert.DoesNotContain("command:sudo route add -host 127.0.0.1 192.168.1.1", operations);
    }

    [Fact]
    public async Task RunAsync_WhenTunCleanupFails_StillClosesOpenedUtun()
    {
        // Arrange
        var operations = new List<string>();
        var device = new RecordingTunDevice(operations, interfaceName: "utun9");
        var commandRunner = new ThrowingCleanupCommandRunner(operations);
        var proxy = new RecordingTunProxy(operations);
        var runtime = new TunDemoRuntime(
            CreateTunDevice: () => device,
            CreateConfigurator: () => new MacTunConfigurator(new MacRouteCommandBuilder(), commandRunner),
            CreateRawTunProxy: (_, _, _, _, _) => proxy);
        DotnetTunDemoCommand command = DotnetTunDemoCommand.Parse(
            [
                "tun",
                "--fake-ip", "198.18.0.1",
                "--domain", "api.anthropic.com",
                "--socks5", "127.0.0.1:7890"
            ],
            runtime);

        // Act
        async Task RunAsync() => await command.RunAsync(TestContext.Current.CancellationToken);

        // Assert
        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(RunAsync);
        Assert.Contains("cleanup failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("close:123", operations);
    }

    [Fact]
    public async Task RunAsync_WhenTunRunIsCancelled_CleansUpAndClosesOpenedUtun()
    {
        // Arrange
        var operations = new List<string>();
        var device = new RecordingTunDevice(operations, interfaceName: "utun9");
        var commandRunner = new RecordingMacCommandRunner(operations);
        var proxy = new BlockingCancellationTunProxy(operations);
        var runtime = new TunDemoRuntime(
            CreateTunDevice: () => device,
            CreateConfigurator: () => new MacTunConfigurator(new MacRouteCommandBuilder(), commandRunner),
            CreateRawTunProxy: (_, _, _, _, _) => proxy);
        DotnetTunDemoCommand command = DotnetTunDemoCommand.Parse(
            [
                "tun",
                "--fake-ip", "198.18.0.1",
                "--domain", "api.anthropic.com",
                "--socks5", "127.0.0.1:7890"
            ],
            runtime);
        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<int> runTask = command.RunAsync(stopSource.Token);
        await proxy.RunStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Act
        await stopSource.CancelAsync();
        int exitCode = await runTask.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, exitCode);
        Assert.Contains(operations, operation => operation == "run:123");
        Assert.Contains(operations, operation => operation.StartsWith("command:sudo route delete", StringComparison.Ordinal));
        Assert.Equal("close:123", operations[^1]);
    }

    private sealed class RecordingTunDevice(List<string> operations, string interfaceName) : ITunDevice
    {
        public Task<TunDeviceOpenResult> OpenTunAsync(CancellationToken cancellationToken = default)
        {
            operations.Add($"open:{interfaceName}");
            return Task.FromResult(TunDeviceOpenResult.Opened(123, interfaceName));
        }

        public ValueTask<TunPacketIoResult> ReadPacketAsync(int fileDescriptor, Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TunPacketIoResult.Transferred(0));

        public ValueTask<TunPacketIoResult> WritePacketAsync(int fileDescriptor, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TunPacketIoResult.Transferred(packet.Length));

        public ValueTask<TunDeviceCloseResult> CloseTunAsync(int fileDescriptor, CancellationToken cancellationToken = default)
        {
            operations.Add($"close:{fileDescriptor}");
            return ValueTask.FromResult(TunDeviceCloseResult.Closed());
        }
    }

    private sealed class RecordingMacCommandRunner(List<string> operations) : IMacCommandRunner
    {
        public ValueTask RunAsync(MacCommand command, CancellationToken cancellationToken = default)
        {
            operations.Add($"command:{command}");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingCleanupCommandRunner(List<string> operations) : IMacCommandRunner
    {
        public ValueTask RunAsync(MacCommand command, CancellationToken cancellationToken = default)
        {
            operations.Add($"command:{command}");
            if (command.Arguments.SequenceEqual(["route", "delete", "-net", "198.18.0.1/32"]))
            {
                throw new InvalidOperationException("cleanup failed");
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingTunProxy(List<string> operations) : ITunDemoRawTunProxy
    {
        public Task RunOpenAsync(int fileDescriptor, CancellationToken cancellationToken = default)
        {
            operations.Add($"run:{fileDescriptor}");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingCancellationTunProxy(List<string> operations) : ITunDemoRawTunProxy
    {
        public TaskCompletionSource RunStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RunOpenAsync(int fileDescriptor, CancellationToken cancellationToken = default)
        {
            operations.Add($"run:{fileDescriptor}");
            RunStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
