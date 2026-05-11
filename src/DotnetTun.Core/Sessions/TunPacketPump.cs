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
        await _tunDevice.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RunOpenAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await _tunDevice.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    public async Task RunOpenAsync(CancellationToken cancellationToken = default)
    {
        using var runSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task? outboundWriteTask = _outboundPackets is null
            ? null
            : WriteOutboundPacketsAsync(_outboundPackets, runSource.Token);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await PumpOnceAsync(runSource.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException exception) when (IsExpectedCancellation(exception, cancellationToken))
        {
            IgnoreExpectedCancellation(exception);
        }
        catch (IOException exception) when (cancellationToken.IsCancellationRequested)
        {
            IgnoreExpectedReadFailureAfterCancellation(exception);
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

    public async ValueTask PumpOnceAsync(CancellationToken cancellationToken = default)
    {
        var readBuffer = new byte[_mtu];
        var bytesTransferred = await _tunDevice.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
        if (bytesTransferred == 0)
        {
            return;
        }

        if (bytesTransferred > readBuffer.Length)
        {
            throw new InvalidOperationException("TUN packet read returned more bytes than the supplied buffer can hold.");
        }

        var packet = readBuffer.AsSpan(0, bytesTransferred).ToArray();
        var responses = await _handler.HandleAsync(packet, cancellationToken).ConfigureAwait(false);
        foreach (var response in responses)
        {
            await WritePacketAsync(response, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteOutboundPacketsAsync(ChannelReader<ReadOnlyMemory<byte>> packets, CancellationToken cancellationToken)
    {
        await foreach (var packet in packets.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await WritePacketAsync(packet, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask WritePacketAsync(ReadOnlyMemory<byte> packet, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _tunDevice.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
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

    private static void IgnoreExpectedReadFailureAfterCancellation(IOException exception)
        => _ = exception;
}
