using DotnetTun.Abstractions;
using DotnetTun.Core.Sessions;
using Xunit;

namespace DotnetTun.Core.Tests.Sessions;

public sealed class TunPacketPumpTests
{
    [Fact]
    public async Task PumpOnceAsync_WhenHandlerReturnsResponse_WritesResponsePacket()
    {
        // Arrange
        byte[] packet = [0x45, 0x00, 0x00, 0x14];
        byte[] response = [0x60, 0x00];
        var device = new FakeTunDevice(packet);
        var handler = new RespondingPacketHandler(response);
        var pump = new TunPacketPump(device, handler, mtu: 8);

        // Act
        await pump.PumpOnceAsync(123, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(packet, handler.ReceivedPacket);
        Assert.Collection(
            device.WrittenPackets,
            writtenPacket =>
            {
                Assert.Equal(123, writtenPacket.FileDescriptor);
                Assert.Equal(response, writtenPacket.Packet);
            });
    }

    [Fact]
    public async Task RunAsync_WhenCancellationRequestedAfterPacket_ClosesTunDevice()
    {
        // Arrange
        byte[] packet = [0x45, 0x00, 0x00, 0x14];
        var device = new FakeTunDevice(packet);
        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var handler = new CancellingPacketHandler(stopSource.Cancel);
        var pump = new TunPacketPump(device, handler, mtu: 8);

        // Act
        await pump.RunAsync(stopSource.Token);

        // Assert
        Assert.Equal(packet, handler.ReceivedPacket);
        Assert.Equal(123, device.ClosedFileDescriptor);
    }

    [Fact]
    public async Task RunAsync_WhenOutboundPacketArrivesWhileTunReadIsPending_WritesPacketToTun()
    {
        // Arrange
        byte[] outboundPacket = [0x45, 0x00, 0x00, 0x14];
        var device = new BlockingReadTunDevice();
        var queue = new TunOutboundPacketQueue();
        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pump = new TunPacketPump(device, new RespondingPacketHandler([]), mtu: 8, outboundPackets: queue);
        Task runTask = pump.RunAsync(stopSource.Token);
        await device.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Act
        await queue.WriteAsync(outboundPacket, TestContext.Current.CancellationToken);
        WrittenPacket writtenPacket = await device.NextWrittenPacketAsync(TestContext.Current.CancellationToken);
        await stopSource.CancelAsync();
        await runTask;

        // Assert
        Assert.Equal(123, writtenPacket.FileDescriptor);
        Assert.Equal(outboundPacket, writtenPacket.Packet);
        Assert.Equal(123, device.ClosedFileDescriptor);
    }

    [Fact]
    public async Task RunAsync_WhenCancellationUnblocksReadAsFailure_ClosesTunWithoutThrowingReadFailure()
    {
        // Arrange
        var device = new CancelledFailedReadTunDevice();
        using var stopSource = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pump = new TunPacketPump(device, new RespondingPacketHandler([]), mtu: 8);
        Task runTask = pump.RunAsync(stopSource.Token);
        await device.ReadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        // Act
        await stopSource.CancelAsync();
        await runTask.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(123, device.ClosedFileDescriptor);
    }

    [Fact]
    public async Task WriteAsync_WhenQueueIsFull_WaitsForReaderBackpressure()
    {
        // Arrange
        var queue = new TunOutboundPacketQueue(capacity: 1);
        await queue.WriteAsync(new byte[] { 0x01 }, TestContext.Current.CancellationToken);

        // Act
        ValueTask secondWrite = queue.WriteAsync(new byte[] { 0x02 }, TestContext.Current.CancellationToken);
        Task secondWriteTask = secondWrite.AsTask();
        Task completedTask = await Task.WhenAny(secondWriteTask, Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken));
        Assert.NotSame(secondWriteTask, completedTask);

        ReadOnlyMemory<byte> firstPacket = await queue.Reader.ReadAsync(TestContext.Current.CancellationToken);
        await secondWriteTask;
        ReadOnlyMemory<byte> secondPacket = await queue.Reader.ReadAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(new byte[] { 0x01 }, firstPacket.ToArray());
        Assert.Equal(new byte[] { 0x02 }, secondPacket.ToArray());
    }

    private sealed class FakeTunDevice(byte[] packet) : ITunDevice
    {
        public List<WrittenPacket> WrittenPackets { get; } = [];

        public int ClosedFileDescriptor { get; private set; } = -1;

        public Task<TunDeviceOpenResult> OpenTunAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(TunDeviceOpenResult.Opened(123, "tun-test"));

        public ValueTask<TunPacketIoResult> ReadPacketAsync(int fileDescriptor, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            packet.CopyTo(buffer);
            return ValueTask.FromResult(TunPacketIoResult.Transferred(packet.Length));
        }

        public ValueTask<TunPacketIoResult> WritePacketAsync(int fileDescriptor, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            WrittenPackets.Add(new WrittenPacket(fileDescriptor, packet.ToArray()));
            return ValueTask.FromResult(TunPacketIoResult.Transferred(packet.Length));
        }

        public ValueTask<TunDeviceCloseResult> CloseTunAsync(int fileDescriptor, CancellationToken cancellationToken = default)
        {
            ClosedFileDescriptor = fileDescriptor;
            return ValueTask.FromResult(TunDeviceCloseResult.Closed());
        }
    }

    private sealed class RespondingPacketHandler(byte[] response) : ITunPacketHandler
    {
        public byte[] ReceivedPacket { get; private set; } = [];

        public ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            ReceivedPacket = packet.ToArray();
            IReadOnlyList<ReadOnlyMemory<byte>> responses = [response];
            return ValueTask.FromResult(responses);
        }
    }

    private sealed class CancellingPacketHandler(Action cancel) : ITunPacketHandler
    {
        public byte[] ReceivedPacket { get; private set; } = [];

        public ValueTask<IReadOnlyList<ReadOnlyMemory<byte>>> HandleAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            ReceivedPacket = packet.ToArray();
            cancel();
            IReadOnlyList<ReadOnlyMemory<byte>> responses = [];
            return ValueTask.FromResult(responses);
        }
    }

    private sealed class BlockingReadTunDevice : ITunDevice
    {
        private readonly TaskCompletionSource<WrittenPacket> _writtenPacket = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ClosedFileDescriptor { get; private set; } = -1;

        public Task<TunDeviceOpenResult> OpenTunAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(TunDeviceOpenResult.Opened(123, "tun-test"));

        public ValueTask<TunPacketIoResult> ReadPacketAsync(int fileDescriptor, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            return new ValueTask<TunPacketIoResult>(WaitForCancellationAsync(cancellationToken));
        }

        public ValueTask<TunPacketIoResult> WritePacketAsync(int fileDescriptor, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
        {
            var writtenPacket = new WrittenPacket(fileDescriptor, packet.ToArray());
            _writtenPacket.TrySetResult(writtenPacket);
            return ValueTask.FromResult(TunPacketIoResult.Transferred(packet.Length));
        }

        public ValueTask<TunDeviceCloseResult> CloseTunAsync(int fileDescriptor, CancellationToken cancellationToken = default)
        {
            ClosedFileDescriptor = fileDescriptor;
            return ValueTask.FromResult(TunDeviceCloseResult.Closed());
        }

        public Task<WrittenPacket> NextWrittenPacketAsync(CancellationToken cancellationToken)
            => _writtenPacket.Task.WaitAsync(cancellationToken);

        private static async Task<TunPacketIoResult> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            return TunPacketIoResult.Transferred(0);
        }
    }

    private sealed class CancelledFailedReadTunDevice : ITunDevice
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ClosedFileDescriptor { get; private set; } = -1;

        public Task<TunDeviceOpenResult> OpenTunAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(TunDeviceOpenResult.Opened(123, "tun-test"));

        public async ValueTask<TunPacketIoResult> ReadPacketAsync(int fileDescriptor, Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return TunPacketIoResult.Failed(89);
            }

            return TunPacketIoResult.Transferred(0);
        }

        public ValueTask<TunPacketIoResult> WritePacketAsync(int fileDescriptor, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(TunPacketIoResult.Transferred(packet.Length));

        public ValueTask<TunDeviceCloseResult> CloseTunAsync(int fileDescriptor, CancellationToken cancellationToken = default)
        {
            ClosedFileDescriptor = fileDescriptor;
            return ValueTask.FromResult(TunDeviceCloseResult.Closed());
        }
    }

    private sealed record WrittenPacket(int FileDescriptor, byte[] Packet);
}
