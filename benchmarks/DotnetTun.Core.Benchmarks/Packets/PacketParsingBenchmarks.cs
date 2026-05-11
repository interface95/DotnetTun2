using BenchmarkDotNet.Attributes;
using DotnetTun.Core.Benchmarks.Support;
using DotnetTun.Core.Packets;

namespace DotnetTun.Core.Benchmarks.Packets;

[MemoryDiagnoser]
[ShortRunJob]
public class PacketParsingBenchmarks
{
    private byte[] _packet = [];

    [Params(0, 32, 512)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var payload = BenchmarkPayload.Create(PayloadSize);
        _packet = BenchmarkPacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Psh | TcpFlags.Ack,
            payload: payload);
    }

    [Benchmark]
    public bool Parse_TcpIpv4Packet()
        => Ipv4Packet.TryParse(_packet, out var ipv4Packet)
            && TcpSegment.TryParse(ipv4Packet, out var tcpSegment)
            && TcpChecksum.IsValid(ipv4Packet, tcpSegment);
}
