using BenchmarkDotNet.Attributes;
using DotnetTun.Core.Benchmarks.Support;
using DotnetTun.Core.Packets;
using DotnetTun.Core.Sessions;

namespace DotnetTun.Core.Benchmarks.Sessions;

[MemoryDiagnoser]
[ShortRunJob]
[RunOncePerIteration]
public class TcpPipelineBenchmarks
{
    private readonly NoopPayloadSink _payloadSink = new();
    private byte[] _payload = [];
    private byte[] _synPacket = [];
    private byte[] _ackPacket = [];
    private byte[] _payloadPacket = [];
    private Ipv4Packet _payloadIpv4Packet;
    private TcpSegment _payloadSegment;
    private RawTcpSessionHandler _handler = null!;

    [Params(32, 512)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _payload = BenchmarkPayload.Create(PayloadSize);
        _synPacket = BenchmarkPacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_000,
            acknowledgmentNumber: 0,
            flags: TcpFlags.Syn);
        _ackPacket = BenchmarkPacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Ack);
        _payloadPacket = BenchmarkPacketFixtures.CreateTcpPacket(
            sourceAddress: [10, 0, 0, 2],
            destinationAddress: [198, 18, 0, 1],
            sourcePort: 54321,
            destinationPort: 443,
            sequenceNumber: 1_001,
            acknowledgmentNumber: 9_001,
            flags: TcpFlags.Psh | TcpFlags.Ack,
            payload: _payload);

        if (!Ipv4Packet.TryParse(_payloadPacket, out _payloadIpv4Packet) || !TcpSegment.TryParse(_payloadIpv4Packet, out _payloadSegment))
        {
            throw new InvalidOperationException("Failed to parse benchmark payload packet.");
        }
    }

    [IterationSetup]
    public void IterationSetup()
        => ResetEstablishedSessionAsync().GetAwaiter().GetResult();

    [Benchmark]
    public async Task<int> Handle_EstablishedPayloadPacket()
    {
        var responses = await _handler.HandleAsync(_payloadIpv4Packet, _payloadSegment).ConfigureAwait(false);
        return responses.Count;
    }

    private async Task ResetEstablishedSessionAsync()
    {
        _handler = new RawTcpSessionHandler(new TcpSessionTable(), serverInitialSequence: 9_000, _payloadSink);
        await HandleTcpPacketAsync(_handler, _synPacket).ConfigureAwait(false);
        await HandleTcpPacketAsync(_handler, _ackPacket).ConfigureAwait(false);
    }

    private static async ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleTcpPacketAsync(RawTcpSessionHandler handler, byte[] packet)
    {
        if (!Ipv4Packet.TryParse(packet, out var ipv4Packet) || !TcpSegment.TryParse(ipv4Packet, out var tcpSegment))
        {
            return [];
        }

        return await handler.HandleAsync(ipv4Packet, tcpSegment).ConfigureAwait(false);
    }

    private sealed class NoopPayloadSink : ITcpPayloadSink
    {
        public ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> WriteAsync(TcpSession session, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<ReadOnlyMemory<byte>>>([]);

        public ValueTask CloseAsync(TcpSession session, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }
}
