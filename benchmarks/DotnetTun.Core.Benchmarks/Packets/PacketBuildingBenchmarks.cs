using System.Net;
using BenchmarkDotNet.Attributes;
using DotnetTun.Core.Benchmarks.Support;
using DotnetTun.Core.Packets;

namespace DotnetTun.Core.Benchmarks.Packets;

[MemoryDiagnoser]
[ShortRunJob]
public class PacketBuildingBenchmarks
{
    private static readonly IPAddress SourceAddress = IPAddress.Parse("198.18.0.1");
    private static readonly IPAddress DestinationAddress = IPAddress.Parse("10.0.0.2");

    private byte[] _payload = [];

    [Params(0, 64)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
        => _payload = BenchmarkPayload.Create(PayloadSize);

    [Benchmark]
    public byte[] Build_TcpPacket()
        => TcpPacketBuilder.Build(
            SourceAddress,
            DestinationAddress,
            sourcePort: 443,
            destinationPort: 54321,
            sequenceNumber: 9_001,
            acknowledgmentNumber: 1_002,
            flags: PayloadSize == 0 ? TcpFlags.Ack : TcpFlags.Psh | TcpFlags.Ack,
            payload: _payload);
}
