using DotnetTun.Abstractions;
using System.Threading.Channels;

namespace DotnetTun.Core.Sessions;

public sealed class TunPacketPump(ITunDevice tunDevice, ITunPacketHandler handler, int mtu = 1500, TunOutboundPacketQueue? outboundPackets = null)
{
    private readonly ITunDevice _tunDevice = tunDevice ?? throw new ArgumentNullException(nameof(tunDevice));
    private readonly ITunPacketHandler _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    private readonly int _mtu = ValidateMtu(mtu);
    private readonly ChannelReader<ReadOnlyMemory<byte>>? _outboundPackets = outboundPackets?.Reader;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        TunDeviceOpenResult openResult = await _tunDevice.OpenTunAsync(cancellationToken).ConfigureAwait(false);
        if (!openResult.Success)
        {
            throw new IOException($"TUN device open failed with error {openResult.ErrorNumber}.");
        }

        int fileDescriptor = openResult.FileDescriptor;
        try
        {
            await RunOpenAsync(fileDescriptor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TunDeviceCloseResult closeResult = await _tunDevice.CloseTunAsync(fileDescriptor, CancellationToken.None).ConfigureAwait(false);
            if (!closeResult.Success)
            {
                throw new IOException($"TUN device close failed with error {closeResult.ErrorNumber}.");
            }
        }
    }

    public async Task RunOpenAsync(int fileDescriptor, CancellationToken cancellationToken = default)
    {
        using var runSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? outboundWriteTask = _outboundPackets is null
            ? null
            : WriteOutboundPacketsAsync(fileDescriptor, _outboundPackets, runSource.Token);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await PumpOnceAsync(fileDescriptor, runSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (IsExpectedCancellation(exception, cancellationToken))
        {
            IgnoreExpectedCancellation(exception);
        }
        finally
        {
            await runSource.CancelAsync().ConfigureAwait(false);
            if (outboundWriteTask is not null)
            {
                try
                {
                    await outboundWriteTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (IsExpectedCancellation(exception, runSource.Token))
                {
                    IgnoreExpectedCancellation(exception);
                }
            }
        }
    }

    public async ValueTask PumpOnceAsync(int fileDescriptor, CancellationToken cancellationToken = default)
    {
        byte[] readBuffer = new byte[_mtu];
        TunPacketIoResult readResult = await _tunDevice.ReadPacketAsync(fileDescriptor, readBuffer, cancellationToken).ConfigureAwait(false);
        if (!readResult.Success)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new IOException($"TUN packet read failed with error {readResult.ErrorNumber}.");
        }

        if (readResult.BytesTransferred == 0)
        {
            return;
        }

        if (readResult.BytesTransferred > readBuffer.Length)
        {
            throw new InvalidOperationException("TUN packet read returned more bytes than the supplied buffer can hold.");
        }

        byte[] packet = readBuffer.AsSpan(0, readResult.BytesTransferred).ToArray();
        IReadOnlyList<ReadOnlyMemory<byte>> responses = await _handler.HandleAsync(packet, cancellationToken).ConfigureAwait(false);
        foreach (ReadOnlyMemory<byte> response in responses)
        {
            await WritePacketAsync(fileDescriptor, response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteOutboundPacketsAsync(int fileDescriptor, ChannelReader<ReadOnlyMemory<byte>> packets, CancellationToken cancellationToken)
    {
        await foreach (ReadOnlyMemory<byte> packet in packets.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await WritePacketAsync(fileDescriptor, packet, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask WritePacketAsync(int fileDescriptor, ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TunPacketIoResult writeResult = await _tunDevice.WritePacketAsync(fileDescriptor, packet, cancellationToken).ConfigureAwait(false);
            if (!writeResult.Success)
            {
                throw new IOException($"TUN packet write failed with error {writeResult.ErrorNumber}.");
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static int ValidateMtu(int mtu)
        => mtu > 0 ? mtu : throw new ArgumentOutOfRangeException(nameof(mtu), mtu, "MTU must be greater than zero.");

    private static bool IsExpectedCancellation(OperationCanceledException exception, CancellationToken cancellationToken)
        => cancellationToken.IsCancellationRequested;

    private static void IgnoreExpectedCancellation(OperationCanceledException exception)
        => _ = exception;
}
