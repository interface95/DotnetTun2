namespace DotnetTun.Platforms.MacOS.Networking;

public sealed class MacTunConfigurator(MacRouteCommandBuilder commandBuilder, IMacCommandRunner commandRunner)
{
    private const string RollbackCleanupExceptionDataKey = "DotnetTun.Platforms.MacOS.RollbackCleanupException";

    private readonly MacRouteCommandBuilder _commandBuilder = commandBuilder ?? throw new ArgumentNullException(nameof(commandBuilder));
    private readonly IMacCommandRunner _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));

    public async ValueTask ConfigureAsync(MacTunOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.ExcludedIps.Count > 0 && options.DefaultGateway is null)
        {
            throw new InvalidOperationException("A default gateway is required when excluded IP routes are configured.");
        }

        try
        {
            foreach (var command in _commandBuilder.BuildConfigureCommands(options))
            {
                await _commandRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
            }

            if (options.DefaultGateway is not null)
            {
                foreach (var command in _commandBuilder.BuildExcludeCommands(options, options.DefaultGateway))
                {
                    await _commandRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception configureException)
        {
            await TryCleanupAfterConfigureFailureAsync(options, configureException).ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask CleanupAsync(MacTunOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (var command in _commandBuilder.BuildExcludeCleanupCommands(options))
        {
            await _commandRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        }

        foreach (var command in _commandBuilder.BuildCleanupCommands(options))
        {
            await _commandRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask TryCleanupAfterConfigureFailureAsync(MacTunOptions options, Exception configureException)
    {
        try
        {
            await CleanupAsync(options, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception cleanupException)
        {
            // Rollback is best-effort; preserve the original configure failure for callers.
            configureException.Data[RollbackCleanupExceptionDataKey] = cleanupException;
        }
    }
}
