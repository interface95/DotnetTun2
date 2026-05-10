using System.Net;
using System.Net.Sockets;

namespace DotnetTun.Core.Sessions;

public sealed class FakeIpTcpBridgeServer(
    FakeIpTcpBridge bridge,
    IPAddress listenAddress,
    int port,
    IPAddress fakeIp,
    int targetPort,
    int maxActiveConnections = 1024) : IAsyncDisposable
{
    private readonly object _connectionLock = new();
    private readonly List<Task> _connections = [];
    private readonly SemaphoreSlim _connectionSlots = new(ValidateMaxActiveConnections(maxActiveConnections));
    private TcpListener? _listener;
    private CancellationTokenSource? _stopSource;
    private Task? _acceptTask;

    public int Port
    {
        get
        {
            if (_listener?.LocalEndpoint is not IPEndPoint endpoint)
            {
                return port;
            }

            return endpoint.Port;
        }
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is not null)
        {
            return Task.CompletedTask;
        }

        if (port is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Listen port must be between 0 and 65535.");
        }

        if (targetPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(targetPort), "Target port must be between 1 and 65535.");
        }

        _listener = new TcpListener(listenAddress, port);
        _listener.Start();
        _stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _acceptTask = AcceptLoopAsync(_stopSource.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_listener is null)
        {
            return;
        }

        await _stopSource?.CancelAsync()!;
        _listener.Stop();

        if (_acceptTask is not null)
        {
            await _acceptTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        Task[] connections;
        lock (_connectionLock)
        {
            connections = [.. _connections];
            _connections.Clear();
        }

        if (connections.Length > 0)
        {
            try
            {
                await Task.WhenAll(connections).WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (IsServerStopCancellation(exception, cancellationToken))
            {
                // Active bridge copies observe the server stop token during normal shutdown.
            }
        }

        _listener = null;
        _stopSource?.Dispose();
        _stopSource = null;
        _acceptTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _connectionSlots.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        TcpListener listener = _listener ?? throw new InvalidOperationException("TCP bridge server is not started.");

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            if (!_connectionSlots.Wait(0))
            {
                client.Dispose();
                continue;
            }

            TrackConnection(HandleClientWithSlotAsync(client, cancellationToken));
        }
    }

    private static int ValidateMaxActiveConnections(int value)
    {
        if (value < 1)
        {
            throw new ArgumentOutOfRangeException("maxActiveConnections", "TCP bridge active connection limit must be at least 1.");
        }

        return value;
    }

    private bool IsServerStopCancellation(OperationCanceledException exception, CancellationToken cancellationToken)
        => _stopSource?.IsCancellationRequested == true
            && (!cancellationToken.IsCancellationRequested || exception.CancellationToken != cancellationToken);

    private void TrackConnection(Task task)
    {
        lock (_connectionLock)
        {
            _connections.Add(task);
        }

        _ = task.ContinueWith(
            completedTask =>
            {
                lock (_connectionLock)
                {
                    _connections.Remove(completedTask);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            await using NetworkStream stream = client.GetStream();
            await bridge.BridgeAsync(stream, fakeIp, targetPort, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleClientWithSlotAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            await HandleClientAsync(client, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _connectionSlots.Release();
        }
    }
}
