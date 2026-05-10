using System.Net;

namespace DotnetTun.Core.Sessions;

public sealed record TcpFlowKey(IPAddress SourceAddress, int SourcePort, IPAddress DestinationAddress, int DestinationPort);
