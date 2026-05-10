namespace DotnetTun.Platforms.MacOS.Networking;

public interface IMacCommandRunner
{
    ValueTask RunAsync(MacCommand command, CancellationToken cancellationToken = default);
}
