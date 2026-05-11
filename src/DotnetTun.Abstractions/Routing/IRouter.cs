namespace DotnetTun.Abstractions.Routing;

public interface IRouter
{
    ValueTask<RouteDecision> RouteAsync(ConnectionContext context, CancellationToken cancellationToken = default);
}
