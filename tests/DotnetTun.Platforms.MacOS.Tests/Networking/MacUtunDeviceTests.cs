using DotnetTun.Abstractions;
using DotnetTun.Platforms.MacOS.Networking;
using Xunit;

namespace DotnetTun.Platforms.MacOS.Tests.Networking;

public sealed class MacUtunDeviceTests
{
    [Fact]
    public void Constructor_WithNullApi_ThrowsArgumentNullException()
    {
        // Arrange / Act
        var act = () => new MacUtunDevice(null!);

        // Assert
        Assert.Throws<ArgumentNullException>(act);
    }

    [Fact]
    public async Task OpenAsync_WhenNativeApiFails_ReturnsFailureResult()
    {
        // Arrange
        var device = new MacUtunDevice(new FailingUtunNativeApi());

        // Act
        MacUtunOpenResult result = await device.OpenAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(42, result.ErrorNumber);
        Assert.Null(result.InterfaceName);
    }

    [Fact]
    public async Task OpenTunAsync_WhenNativeApiOpensUtun_ReturnsSharedTunResult()
    {
        // Arrange
        ITunDevice device = new MacUtunDevice(new SuccessfulUtunNativeApi(123, "utun9"));

        // Act
        TunDeviceOpenResult result = await device.OpenTunAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(123, result.FileDescriptor);
        Assert.Equal("utun9", result.InterfaceName);
        Assert.Equal(0, result.ErrorNumber);
    }

    [Fact]
    public async Task ReadPacketAsync_WhenNativeApiReadsPacket_ReturnsBytesTransferred()
    {
        // Arrange
        byte[] packet = [0x45, 0x00, 0x00, 0x14];
        ITunDevice device = new MacUtunDevice(new ReadingUtunNativeApi(packet));
        byte[] buffer = new byte[8];

        // Act
        TunPacketIoResult result = await device.ReadPacketAsync(123, buffer, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(packet.Length, result.BytesTransferred);
        Assert.Equal(0, result.ErrorNumber);
        Assert.Equal(packet, buffer[..packet.Length]);
    }

    [Fact]
    public async Task ReadPacketAsync_WhenCancelledWhileNativeReadIsBlocked_ClosesFileDescriptorAndThrowsCancellation()
    {
        // Arrange
        const int fileDescriptor = 123;
        const int readFailureErrorNumber = 89;
        var nativeApi = new BlockingReadUntilCloseUtunNativeApi(readFailureErrorNumber);
        ITunDevice device = new MacUtunDevice(nativeApi);
        byte[] buffer = new byte[8];
        using var cancellationSource = new CancellationTokenSource();

        Task<TunPacketIoResult> readTask = Task.Run(
            async () => await device.ReadPacketAsync(fileDescriptor, buffer, cancellationSource.Token),
            TestContext.Current.CancellationToken);

        Assert.True(await nativeApi.WaitForReadStartedAsync(TestContext.Current.CancellationToken));

        try
        {
            // Act
            await cancellationSource.CancelAsync();
            OperationCanceledException exception = await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await readTask.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

            // Assert
            Assert.Equal(cancellationSource.Token, exception.CancellationToken);
            Assert.Equal(fileDescriptor, nativeApi.ClosedFileDescriptor);
        }
        finally
        {
            nativeApi.ReleaseBlockedRead();
        }
    }

    [Fact]
    public async Task CloseTunAsync_WhenReadCancellationAlreadyClosedDescriptor_ReturnsSuccessWithoutClosingAgain()
    {
        // Arrange
        const int fileDescriptor = 123;
        var nativeApi = new BlockingReadUntilCloseUtunNativeApi(readFailureErrorNumber: 9);
        ITunDevice device = new MacUtunDevice(nativeApi);
        byte[] buffer = new byte[8];
        using var cancellationSource = new CancellationTokenSource();
        Task<TunPacketIoResult> readTask = Task.Run(
            async () => await device.ReadPacketAsync(fileDescriptor, buffer, cancellationSource.Token),
            TestContext.Current.CancellationToken);
        Assert.True(await nativeApi.WaitForReadStartedAsync(TestContext.Current.CancellationToken));

        try
        {
            await cancellationSource.CancelAsync();
            _ = await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await readTask.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

            // Act
            TunDeviceCloseResult result = await device.CloseTunAsync(fileDescriptor, TestContext.Current.CancellationToken);

            // Assert
            Assert.True(result.Success);
            Assert.Equal(1, nativeApi.CloseCount);
        }
        finally
        {
            nativeApi.ReleaseBlockedRead();
        }
    }

    [Fact]
    public async Task WritePacketAsync_WhenNativeApiWritesPacket_ReturnsBytesTransferred()
    {
        // Arrange
        byte[] packet = [0x45, 0x00, 0x00, 0x14];
        var nativeApi = new WritingUtunNativeApi();
        ITunDevice device = new MacUtunDevice(nativeApi);

        // Act
        TunPacketIoResult result = await device.WritePacketAsync(123, packet, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(packet.Length, result.BytesTransferred);
        Assert.Equal(0, result.ErrorNumber);
        Assert.Equal(packet, nativeApi.WrittenPacket);
    }

    [Fact]
    public async Task CloseTunAsync_WhenNativeApiClosesDescriptor_ReturnsSuccessResult()
    {
        // Arrange
        var nativeApi = new ClosingUtunNativeApi();
        ITunDevice device = new MacUtunDevice(nativeApi);

        // Act
        TunDeviceCloseResult result = await device.CloseTunAsync(123, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.Success);
        Assert.Equal(0, result.ErrorNumber);
        Assert.Equal(123, nativeApi.ClosedFileDescriptor);
    }

    private sealed class FailingUtunNativeApi : IUtunNativeApi
    {
        public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
        {
            errorNumber = 42;
            return -1;
        }

        public int ReadPacket(int fileDescriptor, Span<byte> buffer, out int errorNumber)
        {
            errorNumber = 42;
            return -1;
        }

        public int WritePacket(int fileDescriptor, ReadOnlySpan<byte> packet, out int errorNumber)
        {
            errorNumber = 42;
            return -1;
        }

        public int Close(int fileDescriptor, out int errorNumber)
        {
            errorNumber = 42;
            return -1;
        }
    }

    private sealed class SuccessfulUtunNativeApi(int fileDescriptor, string interfaceName) : IUtunNativeApi
    {
        public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
        {
            errorNumber = 0;
            byte[] bytes = System.Text.Encoding.ASCII.GetBytes(interfaceName);
            bytes.CopyTo(interfaceNameBuffer);
            interfaceNameBuffer[bytes.Length] = 0;

            return fileDescriptor;
        }

        public int ReadPacket(int fileDescriptor, Span<byte> buffer, out int errorNumber)
        {
            errorNumber = 0;
            return 0;
        }

        public int WritePacket(int fileDescriptor, ReadOnlySpan<byte> packet, out int errorNumber)
        {
            errorNumber = 0;
            return packet.Length;
        }

        public int Close(int fileDescriptor, out int errorNumber)
        {
            errorNumber = 0;
            return 0;
        }
    }

    private sealed class ReadingUtunNativeApi(byte[] packet) : IUtunNativeApi
    {
        public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
        {
            errorNumber = 0;
            return 123;
        }

        public int ReadPacket(int fileDescriptor, Span<byte> buffer, out int errorNumber)
        {
            errorNumber = 0;
            packet.CopyTo(buffer);
            return packet.Length;
        }

        public int WritePacket(int fileDescriptor, ReadOnlySpan<byte> packet, out int errorNumber)
        {
            errorNumber = 0;
            return packet.Length;
        }

        public int Close(int fileDescriptor, out int errorNumber)
        {
            errorNumber = 0;
            return 0;
        }
    }

    private sealed class BlockingReadUntilCloseUtunNativeApi(int readFailureErrorNumber) : IUtunNativeApi
    {
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _closed = new();

        public int ClosedFileDescriptor { get; private set; } = -1;

        public int CloseCount { get; private set; }

        public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
        {
            errorNumber = 0;
            return 123;
        }

        public int ReadPacket(int fileDescriptor, Span<byte> buffer, out int errorNumber)
        {
            _readStarted.SetResult();
            _closed.Wait();
            errorNumber = readFailureErrorNumber;
            return -1;
        }

        public int WritePacket(int fileDescriptor, ReadOnlySpan<byte> packet, out int errorNumber)
        {
            errorNumber = 0;
            return packet.Length;
        }

        public int Close(int fileDescriptor, out int errorNumber)
        {
            errorNumber = 0;
            CloseCount++;
            ClosedFileDescriptor = fileDescriptor;
            _closed.Set();
            return 0;
        }

        public async Task<bool> WaitForReadStartedAsync(CancellationToken cancellationToken)
        {
            await _readStarted.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            return true;
        }

        public void ReleaseBlockedRead() => _closed.Set();
    }

    private sealed class WritingUtunNativeApi : IUtunNativeApi
    {
        public byte[] WrittenPacket { get; private set; } = [];

        public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
        {
            errorNumber = 0;
            return 123;
        }

        public int ReadPacket(int fileDescriptor, Span<byte> buffer, out int errorNumber)
        {
            errorNumber = 0;
            return 0;
        }

        public int WritePacket(int fileDescriptor, ReadOnlySpan<byte> packet, out int errorNumber)
        {
            errorNumber = 0;
            WrittenPacket = packet.ToArray();
            return packet.Length;
        }

        public int Close(int fileDescriptor, out int errorNumber)
        {
            errorNumber = 0;
            return 0;
        }
    }

    private sealed class ClosingUtunNativeApi : IUtunNativeApi
    {
        public int ClosedFileDescriptor { get; private set; } = -1;

        public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
        {
            errorNumber = 0;
            return 123;
        }

        public int ReadPacket(int fileDescriptor, Span<byte> buffer, out int errorNumber)
        {
            errorNumber = 0;
            return 0;
        }

        public int WritePacket(int fileDescriptor, ReadOnlySpan<byte> packet, out int errorNumber)
        {
            errorNumber = 0;
            return packet.Length;
        }

        public int Close(int fileDescriptor, out int errorNumber)
        {
            errorNumber = 0;
            ClosedFileDescriptor = fileDescriptor;
            return 0;
        }
    }
}
