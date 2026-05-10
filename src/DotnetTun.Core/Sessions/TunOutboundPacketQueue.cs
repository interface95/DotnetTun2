using System.Threading.Channels;

namespace DotnetTun.Core.Sessions;

public sealed class TunOutboundPacketQueue(int capacity = TunOutboundPacketQueue.DefaultCapacity)
{
    public const int DefaultCapacity = 1024;

    private readonly Channel<ReadOnlyMemory<byte>> _packets = Channel.CreateBounded<ReadOnlyMemory<byte>>(
        new BoundedChannelOptions(ValidateCapacity(capacity))
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    public ChannelReader<ReadOnlyMemory<byte>> Reader => _packets.Reader;

    public ValueTask WriteAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        => _packets.Writer.WriteAsync(packet.ToArray(), cancellationToken);

    private static int ValidateCapacity(int capacity)
        => capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Queue capacity must be greater than zero.");
}
