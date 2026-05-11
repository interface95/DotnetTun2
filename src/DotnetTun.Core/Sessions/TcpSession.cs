namespace DotnetTun.Core.Sessions;

public readonly record struct TcpSession(
    TcpFlowKey Key,
    uint ClientInitialSequence,
    uint ServerInitialSequence,
    uint NextClientSequence,
    uint NextServerSequence,
    TcpSessionState State);
