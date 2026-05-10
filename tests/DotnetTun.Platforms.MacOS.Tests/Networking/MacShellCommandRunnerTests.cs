using System.Diagnostics;
using DotnetTun.Platforms.MacOS.Networking;
using Xunit;

namespace DotnetTun.Platforms.MacOS.Tests.Networking;

public sealed class MacShellCommandRunnerTests
{
    [Fact]
    public async Task RunAsync_WhenCommandExitsNonZeroAndIgnoreFailureIsFalse_Throws()
    {
        // Arrange
        var runner = new MacShellCommandRunner();
        var command = new MacCommand("/usr/bin/false", []);

        // Act
        async Task RunAsync() => await runner.RunAsync(command, TestContext.Current.CancellationToken);

        // Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(RunAsync);
        Assert.Contains("exit code", exception.Message, StringComparison.Ordinal);
        Assert.Contains("/usr/bin/false", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_WhenCommandExitsNonZeroAndIgnoreFailureIsTrue_DoesNotThrow()
    {
        // Arrange
        var runner = new MacShellCommandRunner();
        var command = new MacCommand("/usr/bin/false", [], IgnoreFailure: true);

        // Act
        await runner.RunAsync(command, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunAsync_WhenCommandWritesLargeStdout_CompletesWithoutDeadlock()
    {
        // Arrange
        var runner = new MacShellCommandRunner();
        var command = new MacCommand("/bin/dd", ["if=/dev/zero", "bs=1024", "count=256"]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act
        await runner.RunAsync(command, cancellation.Token);
    }

    [Fact]
    public async Task RunAsync_WhenCancelled_KillsStartedChildProcessAndRethrowsCancellation()
    {
        // Arrange
        var runner = new MacShellCommandRunner();
        string tempDirectory = Path.Combine(Path.GetTempPath(), "DotnetTun-MacShellCommandRunnerTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        string scriptPath = Path.Combine(tempDirectory, "record-pid-and-sleep.sh");
        string pidPath = Path.Combine(tempDirectory, "child.pid");
        await File.WriteAllTextAsync(
            scriptPath,
            """
            #!/bin/sh
            printf '%s\n' "$$" > "$1"
            sleep 30
            """,
            TestContext.Current.CancellationToken);
        int? childProcessId = null;
        using var cancellation = new CancellationTokenSource();

        try
        {
            Task runTask = runner.RunAsync(new MacCommand("/bin/sh", [scriptPath, pidPath]), cancellation.Token).AsTask();
            childProcessId = await ReadChildProcessIdAsync(pidPath, TestContext.Current.CancellationToken);

            // Act
            await cancellation.CancelAsync();

            // Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.False(await IsProcessRunningAsync(childProcessId.Value, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        }
        finally
        {
            if (childProcessId is not null)
            {
                KillProcessIfStillRunning(childProcessId.Value);
            }

            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static async Task<int> ReadChildProcessIdAsync(string pidPath, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        while (!linked.Token.IsCancellationRequested)
        {
            if (File.Exists(pidPath))
            {
                string text = await File.ReadAllTextAsync(pidPath, cancellationToken);
                if (int.TryParse(text.Trim(), out int processId))
                {
                    return processId;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), linked.Token);
        }

        throw new TimeoutException("The child process did not record its process id.");
    }

    private static async Task<bool> IsProcessRunningAsync(int processId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (!IsProcessRunning(processId))
            {
                return false;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        return IsProcessRunning(processId);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void KillProcessIfStillRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5_000);
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }
}
