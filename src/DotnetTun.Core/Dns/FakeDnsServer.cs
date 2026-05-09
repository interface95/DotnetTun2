using System.Net;
using System.Net.Sockets;

namespace DotnetTun.Core.Dns;

public sealed class FakeDnsServer(FakeDnsResolver resolver, IPAddress listenAddress, int port) : IAsyncDisposable
{
    private UdpClient? _udpClient;
    private CancellationTokenSource? _stopSource;
    private Task? _receiveTask;

    public int Port
    {
        get
        {
            if (_udpClient?.Client.LocalEndPoint is not IPEndPoint endpoint)
            {
                return port;
            }

            return endpoint.Port;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_udpClient is not null)
        {
            return Task.CompletedTask;
        }

        _udpClient = new UdpClient(new IPEndPoint(listenAddress, port));
        _stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _receiveTask = ReceiveLoopAsync(_stopSource.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_udpClient is null)
        {
            return;
        }

        await _stopSource?.CancelAsync()!;
        _udpClient.Dispose();

        if (_receiveTask is not null)
        {
            await _receiveTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        _udpClient = null;
        _stopSource?.Dispose();
        _stopSource = null;
        _receiveTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        UdpClient udpClient = _udpClient ?? throw new InvalidOperationException("DNS server is not started.");

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udpClient.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (resolver.TryResolve(result.Buffer, out byte[]? response))
            {
                await udpClient.SendAsync(response, result.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
