using System.Diagnostics;

namespace DotnetTun.Platforms.MacOS.Networking;

public sealed class MacShellCommandRunner : IMacCommandRunner
{
    public async ValueTask RunAsync(MacCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.Executable))
        {
            throw new ArgumentException("Command executable must not be empty.", nameof(command));
        }

        var startInfo = new ProcessStartInfo(command.Executable)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (string argument in command.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start macOS command process.");
        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            KillProcessTree(process);
            await WaitForKilledProcessAsync(process).ConfigureAwait(false);
            await ObserveOutputTasksAsync(standardOutputTask, standardErrorTask).ConfigureAwait(false);
            throw;
        }

        await standardOutputTask.ConfigureAwait(false);
        string standardError = await standardErrorTask.ConfigureAwait(false);

        if (process.ExitCode != 0 && !command.IgnoreFailure)
        {
            throw new InvalidOperationException($"macOS command failed with exit code {process.ExitCode}: {command}\n{standardError}");
        }
    }

    private static void KillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException exception)
        {
            IgnoreExpectedProcessCleanupException(exception);
        }
    }

    private static async Task WaitForKilledProcessAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            IgnoreExpectedProcessCleanupException(exception);
        }
    }

    private static async Task ObserveOutputTasksAsync(Task<string> standardOutputTask, Task<string> standardErrorTask)
    {
        try
        {
            await Task.WhenAll(standardOutputTask, standardErrorTask).ConfigureAwait(false);
        }
        catch (InvalidOperationException exception)
        {
            IgnoreExpectedProcessCleanupException(exception);
        }
        catch (IOException exception)
        {
            IgnoreExpectedProcessCleanupException(exception);
        }
    }

    private static void IgnoreExpectedProcessCleanupException(Exception exception)
        => _ = exception;
}
