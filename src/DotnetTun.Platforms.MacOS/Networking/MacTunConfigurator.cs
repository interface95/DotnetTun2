namespace DotnetTun.Platforms.MacOS.Networking;

public sealed class MacTunConfigurator(MacRouteCommandBuilder commandBuilder, IMacCommandRunner commandRunner)
{
    private readonly MacRouteCommandBuilder _commandBuilder = commandBuilder ?? throw new ArgumentNullException(nameof(commandBuilder));
    private readonly IMacCommandRunner _commandRunner = commandRunner ?? throw new ArgumentNullException(nameof(commandRunner));

    public async ValueTask ConfigureAsync(MacTunOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (MacCommand command in _commandBuilder.BuildConfigureCommands(options))
        {
            await _commandRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask CleanupAsync(MacTunOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (MacCommand command in _commandBuilder.BuildCleanupCommands(options))
        {
            await _commandRunner.RunAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }
}
