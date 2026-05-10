namespace DotnetTun.Core.Sessions;

public sealed record TcpSession(
    TcpFlowKey Key,
    uint ClientInitialSequence,
    uint ServerInitialSequence,
    uint NextClientSequence,
    uint NextServerSequence,
    TcpSessionState State);
