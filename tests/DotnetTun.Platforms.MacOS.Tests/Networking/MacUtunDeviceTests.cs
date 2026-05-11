using System.IO;
using DotnetTun.Platforms.MacOS.Networking;
using Xunit;

namespace DotnetTun.Platforms.MacOS.Tests.Networking;

public sealed class MacUtunDeviceTests
{
    [Fact]
    public void Constructor_WithNullApi_ThrowsArgumentNullException()
    {
        var act = () => new MacUtunDevice(null!);

        Assert.Throws<ArgumentNullException>(act);
    }

    [Fact]
    public async Task OpenAsync_WhenNativeApiFails_ThrowsAndLeavesDeviceClosed()
    {
        var device = new MacUtunDevice(new OpenFailingUtunNativeApi());

        var exception = await Assert.ThrowsAsync<IOException>(
            async () => await device.OpenAsync(TestContext.Current.CancellationToken));

        Assert.Contains("42", exception.Message, StringComparison.Ordinal);
        Assert.False(device.IsOpen);
        Assert.Null(device.InterfaceName);
    }

    [Fact]
    public async Task OpenAsync_WhenNativeApiOpensUtun_SetsLifecycleState()
    {
        var device = new MacUtunDevice(new SuccessfulUtunNativeApi(123, "utun9"));

        await device.OpenAsync(TestContext.Current.CancellationToken);

        Assert.True(device.IsOpen);
        Assert.Equal("utun9", device.InterfaceName);
    }

    [Fact]
    public async Task OpenAsync_WhenNativeApiReturnsInvalidInterfaceName_ClosesDescriptorAndThrows()
    {
        var nativeApi = new InvalidInterfaceNameUtunNativeApi("en0");
        var device = new MacUtunDevice(nativeApi);

        var exception = await Assert.ThrowsAsync<IOException>(
            async () => await device.OpenAsync(TestContext.Current.CancellationToken));

        Assert.Contains("interface name", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(device.IsOpen);
        Assert.Null(device.InterfaceName);
        Assert.Equal(123, nativeApi.ClosedFileDescriptor);
        Assert.Equal(1, nativeApi.CloseCount);
    }

    [Fact]
    public async Task OpenAsync_WhenConcurrentOpenWinsRace_ClosesExtraDescriptor()
    {
        var nativeApi = new ConcurrentOpeningUtunNativeApi();
        var device = new MacUtunDevice(nativeApi);
        var firstOpenTask = Task.Run(
            async () => await device.OpenAsync(CancellationToken.None),
            TestContext.Current.CancellationToken);
        var secondOpenTask = Task.Run(
            async () => await device.OpenAsync(CancellationToken.None),
            TestContext.Current.CancellationToken);

        Assert.True(await nativeApi.WaitForBothOpenCallsAsync(TestContext.Current.CancellationToken));
        nativeApi.ReleaseOpenCalls();
        await Task.WhenAll(firstOpenTask, secondOpenTask).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.True(device.IsOpen);
        Assert.Equal(2, nativeApi.OpenCount);
        Assert.Equal(1, nativeApi.CloseCount);
        Assert.Contains(nativeApi.ClosedFileDescriptor, nativeApi.OpenedFileDescriptors);
    }

    [Fact]
    public async Task ReadAsync_WhenDeviceIsNotOpen_ThrowsInvalidOperationException()
    {
        var device = new MacUtunDevice(new ReadingUtunNativeApi([0x00, 0x00, 0x00, 0x02]));
        byte[] buffer = new byte[8];

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await device.ReadAsync(buffer, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task WriteAsync_WhenDeviceIsNotOpen_ThrowsInvalidOperationException()
    {
        var device = new MacUtunDevice(new WritingUtunNativeApi());
        byte[] packet = [0x45, 0x00, 0x00, 0x14];

        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await device.WriteAsync(packet, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CloseAsync_WhenDeviceWasNeverOpened_IsNoOp()
    {
        var nativeApi = new ClosingUtunNativeApi();
        var device = new MacUtunDevice(nativeApi);

        await device.CloseAsync(TestContext.Current.CancellationToken);

        Assert.False(device.IsOpen);
        Assert.Null(device.InterfaceName);
        Assert.Equal(0, nativeApi.CloseCount);
    }

    [Fact]
    public async Task CloseAsync_WhenDeviceIsOpen_ClosesDescriptorAndResetsLifecycleState()
    {
        var nativeApi = new ClosingUtunNativeApi();
        var device = new MacUtunDevice(nativeApi);
        await device.OpenAsync(TestContext.Current.CancellationToken);

        await device.CloseAsync(TestContext.Current.CancellationToken);

        Assert.False(device.IsOpen);
        Assert.Null(device.InterfaceName);
        Assert.Equal(123, nativeApi.ClosedFileDescriptor);
        Assert.Equal(1, nativeApi.CloseCount);
    }

    [Fact]
    public async Task CloseAsync_WhenCalledTwice_ClosesNativeDescriptorOnce()
    {
        var nativeApi = new ClosingUtunNativeApi();
        var device = new MacUtunDevice(nativeApi);
        await device.OpenAsync(TestContext.Current.CancellationToken);

        await device.CloseAsync(TestContext.Current.CancellationToken);
        await device.CloseAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, nativeApi.CloseCount);
    }

    [Fact]
    public async Task ReadAsync_WhenNativeApiReadsIpv4Frame_StripsUtunFamilyHeader()
    {
        byte[] packet = [0x45, 0x00, 0x00, 0x14];
        byte[] framedPacket = [0x00, 0x00, 0x00, 0x02, .. packet];
        var device = new MacUtunDevice(new ReadingUtunNativeApi(framedPacket));
        byte[] buffer = new byte[8];
        await device.OpenAsync(TestContext.Current.CancellationToken);

        var bytesTransferred = await device.ReadAsync(buffer, TestContext.Current.CancellationToken);

        Assert.Equal(packet.Length, bytesTransferred);
        Assert.Equal(packet, buffer[..packet.Length]);
    }

    [Fact]
    public async Task ReadAsync_WhenNativeApiReadsIpv6Frame_StripsUtunFamilyHeader()
    {
        byte[] packet = [0x60, 0x00, 0x00, 0x00];
        byte[] framedPacket = [0x00, 0x00, 0x00, 0x1e, .. packet];
        var device = new MacUtunDevice(new ReadingUtunNativeApi(framedPacket));
        byte[] buffer = new byte[8];
        await device.OpenAsync(TestContext.Current.CancellationToken);

        var bytesTransferred = await device.ReadAsync(buffer, TestContext.Current.CancellationToken);

        Assert.Equal(packet.Length, bytesTransferred);
        Assert.Equal(packet, buffer[..packet.Length]);
    }

    [Fact]
    public async Task ReadAsync_WhenNativeApiReadsIpv4FrameWithoutCancellation_DoesNotAllocateFramingBuffer()
    {
        byte[] packet = [0x45, 0x00, 0x00, 0x14];
        byte[] framedPacket = [0x00, 0x00, 0x00, 0x02, .. packet];
        var device = new MacUtunDevice(new ReadingUtunNativeApi(framedPacket));
        byte[] buffer = new byte[8];
        await device.OpenAsync(CancellationToken.None);
        _ = await device.ReadAsync(buffer, CancellationToken.None);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var totalBytesRead = 0;

        for (var i = 0; i < 10; i++)
        {
            totalBytesRead += await device.ReadAsync(buffer, CancellationToken.None);
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(packet.Length * 10, totalBytesRead);
        Assert.Equal(packet, buffer[..packet.Length]);
        Assert.Equal(0, allocatedBytes);
    }

    [Fact]
    public async Task ReadAsync_WhenNativeApiReadsUnsupportedFamilyFrame_ThrowsInvalidDataException()
    {
        byte[] framedPacket = [0x00, 0x00, 0x00, 0x01, 0x45, 0x00, 0x00, 0x14];
        var device = new MacUtunDevice(new ReadingUtunNativeApi(framedPacket));
        byte[] buffer = new byte[8];
        await device.OpenAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await device.ReadAsync(buffer, TestContext.Current.CancellationToken));
        Assert.Equal(new byte[8], buffer);
    }

    [Fact]
    public async Task ReadAsync_WhenNativeApiReadsShortUtunHeader_ThrowsInvalidDataException()
    {
        byte[] shortFrame = [0x00, 0x00, 0x00];
        var device = new MacUtunDevice(new ReadingUtunNativeApi(shortFrame));
        byte[] buffer = new byte[8];
        await device.OpenAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await device.ReadAsync(buffer, TestContext.Current.CancellationToken));
        Assert.Equal(new byte[8], buffer);
    }

    [Fact]
    public async Task ReadAsync_WhenNativeApiReadFails_ThrowsWithNativeErrorNumber()
    {
        var device = new MacUtunDevice(new FailingUtunNativeApi());
        byte[] buffer = new byte[8];
        await device.OpenAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<IOException>(
            async () => await device.ReadAsync(buffer, TestContext.Current.CancellationToken));

        Assert.Contains("42", exception.Message, StringComparison.Ordinal);
        Assert.Equal(new byte[8], buffer);
    }

    [Fact]
    public async Task ReadAsync_WhenCancelledWhileNativeReadIsBlocked_ClosesFileDescriptorAndThrowsCancellation()
    {
        const int readFailureErrorNumber = 89;
        var nativeApi = new BlockingReadUntilCloseUtunNativeApi(readFailureErrorNumber);
        var device = new MacUtunDevice(nativeApi);
        byte[] buffer = new byte[8];
        using var cancellationSource = new CancellationTokenSource();
        await device.OpenAsync(TestContext.Current.CancellationToken);

        var readTask = Task.Run(
            async () => await device.ReadAsync(buffer, cancellationSource.Token),
            TestContext.Current.CancellationToken);

        Assert.True(await nativeApi.WaitForReadStartedAsync(TestContext.Current.CancellationToken));

        try
        {
            await cancellationSource.CancelAsync();
            var exception = await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await readTask.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

            Assert.Equal(cancellationSource.Token, exception.CancellationToken);
            Assert.Equal(123, nativeApi.ClosedFileDescriptor);
            Assert.False(device.IsOpen);
            Assert.Null(device.InterfaceName);
        }
        finally
        {
            nativeApi.ReleaseBlockedRead();
        }
    }

    [Fact]
    public async Task CloseAsync_WhenReadCancellationAlreadyClosedDescriptor_DoesNotCloseAgain()
    {
        var nativeApi = new BlockingReadUntilCloseUtunNativeApi(readFailureErrorNumber: 9);
        var device = new MacUtunDevice(nativeApi);
        byte[] buffer = new byte[8];
        using var cancellationSource = new CancellationTokenSource();
        await device.OpenAsync(TestContext.Current.CancellationToken);
        var readTask = Task.Run(
            async () => await device.ReadAsync(buffer, cancellationSource.Token),
            TestContext.Current.CancellationToken);
        Assert.True(await nativeApi.WaitForReadStartedAsync(TestContext.Current.CancellationToken));

        try
        {
            await cancellationSource.CancelAsync();
            _ = await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await readTask.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

            await device.CloseAsync(TestContext.Current.CancellationToken);

            Assert.False(device.IsOpen);
            Assert.Null(device.InterfaceName);
            Assert.Equal(1, nativeApi.CloseCount);
        }
        finally
        {
            nativeApi.ReleaseBlockedRead();
        }
    }

    [Fact]
    public async Task CloseAsync_WhenReadCancellationCloseIsInProgress_DoesNotCloseAgain()
    {
        var nativeApi = new BlockingCloseDuringCancellationUtunNativeApi(readFailureErrorNumber: 9);
        var device = new MacUtunDevice(nativeApi);
        byte[] buffer = new byte[8];
        using var cancellationSource = new CancellationTokenSource();
        await device.OpenAsync(TestContext.Current.CancellationToken);
        var readTask = Task.Run(
            async () => await device.ReadAsync(buffer, cancellationSource.Token),
            TestContext.Current.CancellationToken);
        Assert.True(await nativeApi.WaitForReadStartedAsync(TestContext.Current.CancellationToken));

        var cancelTask = Task.Run(cancellationSource.Cancel, TestContext.Current.CancellationToken);

        try
        {
            Assert.True(await nativeApi.WaitForCloseEnteredAsync(TestContext.Current.CancellationToken));

            await device.CloseAsync(TestContext.Current.CancellationToken);
            nativeApi.ReleaseClose();
            await cancelTask.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            _ = await Assert.ThrowsAsync<OperationCanceledException>(
                async () => await readTask.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));

            Assert.False(device.IsOpen);
            Assert.Null(device.InterfaceName);
            Assert.Equal(1, nativeApi.CloseCount);
        }
        finally
        {
            nativeApi.ReleaseClose();
        }
    }

    [Fact]
    public async Task WriteAsync_WhenWritingIpv4Packet_PrependsUtunFamilyHeader()
    {
        byte[] packet = [0x45, 0x00, 0x00, 0x14];
        byte[] framedPacket = [0x00, 0x00, 0x00, 0x02, .. packet];
        var nativeApi = new WritingUtunNativeApi();
        var device = new MacUtunDevice(nativeApi);
        await device.OpenAsync(TestContext.Current.CancellationToken);

        await device.WriteAsync(packet, TestContext.Current.CancellationToken);

        Assert.Equal(framedPacket, nativeApi.WrittenPacket);
    }

    [Fact]
    public async Task WriteAsync_WhenWritingIpv6Packet_PrependsUtunFamilyHeader()
    {
        byte[] packet = [0x60, 0x00, 0x00, 0x00];
        byte[] framedPacket = [0x00, 0x00, 0x00, 0x1e, .. packet];
        var nativeApi = new WritingUtunNativeApi();
        var device = new MacUtunDevice(nativeApi);
        await device.OpenAsync(TestContext.Current.CancellationToken);

        await device.WriteAsync(packet, TestContext.Current.CancellationToken);

        Assert.Equal(framedPacket, nativeApi.WrittenPacket);
    }

    [Fact]
    public async Task WriteAsync_WhenWritingIpv4Packet_DoesNotAllocateFramingBuffer()
    {
        byte[] packet = [0x45, 0x00, 0x00, 0x14];
        var nativeApi = new NonCapturingWritingUtunNativeApi();
        var device = new MacUtunDevice(nativeApi);
        await device.OpenAsync(TestContext.Current.CancellationToken);
        await device.WriteAsync(packet, TestContext.Current.CancellationToken);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var totalBytesWritten = 0;

        for (var i = 0; i < 10; i++)
        {
            await device.WriteAsync(packet, TestContext.Current.CancellationToken);
            totalBytesWritten += nativeApi.LastPacketLength;
        }

        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal((packet.Length + MacUtunFrame.HeaderLength) * 10, totalBytesWritten);
        Assert.Equal(0, allocatedBytes);
    }

    [Fact]
    public async Task WriteAsync_WhenPacketIsEmpty_ThrowsInvalidDataExceptionWithoutNativeWrite()
    {
        var nativeApi = new WritingUtunNativeApi();
        var device = new MacUtunDevice(nativeApi);
        await device.OpenAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await device.WriteAsync(Array.Empty<byte>(), TestContext.Current.CancellationToken));

        Assert.Equal(0, nativeApi.WriteCount);
        Assert.Empty(nativeApi.WrittenPacket);
    }

    [Fact]
    public async Task WriteAsync_WhenIpVersionIsUnsupported_ThrowsInvalidDataExceptionWithoutNativeWrite()
    {
        byte[] packet = [0x70, 0x00, 0x00, 0x00];
        var nativeApi = new WritingUtunNativeApi();
        var device = new MacUtunDevice(nativeApi);
        await device.OpenAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await device.WriteAsync(packet, TestContext.Current.CancellationToken));

        Assert.Equal(0, nativeApi.WriteCount);
        Assert.Empty(nativeApi.WrittenPacket);
    }

    [Fact]
    public async Task WriteAsync_WhenNativeApiPartiallyWritesFramedPacket_ThrowsIOException()
    {
        byte[] packet = [0x45, 0x00, 0x00, 0x14];
        var nativeApi = new PartialWritingUtunNativeApi();
        var device = new MacUtunDevice(nativeApi);
        await device.OpenAsync(TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<IOException>(
            async () => await device.WriteAsync(packet, TestContext.Current.CancellationToken));

        Assert.Contains("partial", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, nativeApi.WriteCount);
    }

    private sealed class OpenFailingUtunNativeApi : IUtunNativeApi
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

    private sealed class FailingUtunNativeApi : IUtunNativeApi
    {
        public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
        {
            WriteInterfaceName(interfaceNameBuffer, "utun9");
            errorNumber = 0;
            return 123;
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
            WriteInterfaceName(interfaceNameBuffer, interfaceName);
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

    private sealed class InvalidInterfaceNameUtunNativeApi(string interfaceName) : IUtunNativeApi
    {
        public int ClosedFileDescriptor { get; private set; } = -1;

        public int CloseCount { get; private set; }

        public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
        {
            errorNumber = 0;
            WriteInterfaceName(interfaceNameBuffer, interfaceName);
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
            CloseCount++;
            return 0;
        }
    }

    private sealed class ConcurrentOpeningUtunNativeApi : IUtunNativeApi
    {
        private readonly TaskCompletionSource _bothOpenCallsEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _allowOpenCallsToReturn = new();
        private readonly object _gate = new();
        private readonly int[] _openedFileDescriptors = new int[2];
        private int _nextFileDescriptor = 122;
        private int _openCount;

        public int OpenCount => _openCount;

        public int CloseCount { get; private set; }

        public int ClosedFileDescriptor { get; private set; } = -1;

        public IReadOnlyCollection<int> OpenedFileDescriptors => _openedFileDescriptors;

        public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
        {
            var fileDescriptor = Interlocked.Increment(ref _nextFileDescriptor);
            var openIndex = Interlocked.Increment(ref _openCount) - 1;
            _openedFileDescriptors[openIndex] = fileDescriptor;
            if (openIndex == 1)
            {
                _bothOpenCallsEntered.SetResult();
            }

            _allowOpenCallsToReturn.Wait();
            errorNumber = 0;
            WriteInterfaceName(interfaceNameBuffer, $"utun{openIndex + 1}");
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
            lock (_gate)
            {
                CloseCount++;
                ClosedFileDescriptor = fileDescriptor;
            }

            errorNumber = 0;
            return 0;
        }

        public async Task<bool> WaitForBothOpenCallsAsync(CancellationToken cancellationToken)
        {
            await _bothOpenCallsEntered.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            return true;
        }

        public void ReleaseOpenCalls() => _allowOpenCallsToReturn.Set();
    }

    private sealed class ReadingUtunNativeApi(byte[] packet) : IUtunNativeApi
    {
        public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
        {
            errorNumber = 0;
            WriteInterfaceName(interfaceNameBuffer, "utun9");
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
            WriteInterfaceName(interfaceNameBuffer, "utun9");
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

    private sealed class BlockingCloseDuringCancellationUtunNativeApi(int readFailureErrorNumber) : IUtunNativeApi
    {
        private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closeEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _allowClose = new();
        private readonly ManualResetEventSlim _closed = new();
        private int _closeCount;

        public int CloseCount => _closeCount;

        public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
        {
            errorNumber = 0;
            WriteInterfaceName(interfaceNameBuffer, "utun9");
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
            var closeCount = Interlocked.Increment(ref _closeCount);
            _closeEntered.TrySetResult();
            if (closeCount == 1)
            {
                _allowClose.Wait();
            }

            _closed.Set();
            return 0;
        }

        public async Task<bool> WaitForReadStartedAsync(CancellationToken cancellationToken)
        {
            await _readStarted.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            return true;
        }

        public async Task<bool> WaitForCloseEnteredAsync(CancellationToken cancellationToken)
        {
            await _closeEntered.Task.WaitAsync(TimeSpan.FromSeconds(1), cancellationToken);
            return true;
        }

        public void ReleaseClose() => _allowClose.Set();
    }

    private sealed class WritingUtunNativeApi : IUtunNativeApi
    {
        public byte[] WrittenPacket { get; private set; } = [];

        public int WriteCount { get; private set; }

        public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
        {
            errorNumber = 0;
            WriteInterfaceName(interfaceNameBuffer, "utun9");
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
            WriteCount++;
            WrittenPacket = packet.ToArray();
            return packet.Length;
        }

        public int Close(int fileDescriptor, out int errorNumber)
        {
            errorNumber = 0;
            return 0;
        }
    }

    private sealed class NonCapturingWritingUtunNativeApi : IUtunNativeApi
    {
        public int LastPacketLength { get; private set; }

        public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
        {
            errorNumber = 0;
            WriteInterfaceName(interfaceNameBuffer, "utun9");
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
            LastPacketLength = packet.Length;
            return packet.Length;
        }

        public int Close(int fileDescriptor, out int errorNumber)
        {
            errorNumber = 0;
            return 0;
        }
    }

    private sealed class PartialWritingUtunNativeApi : IUtunNativeApi
    {
        public int WriteCount { get; private set; }

        public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
        {
            errorNumber = 0;
            WriteInterfaceName(interfaceNameBuffer, "utun9");
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
            WriteCount++;
            return packet.Length - 1;
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

        public int CloseCount { get; private set; }

        public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
        {
            errorNumber = 0;
            WriteInterfaceName(interfaceNameBuffer, "utun9");
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
            CloseCount++;
            ClosedFileDescriptor = fileDescriptor;
            return 0;
        }
    }

    private static void WriteInterfaceName(Span<byte> interfaceNameBuffer, string interfaceName)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(interfaceName);
        bytes.CopyTo(interfaceNameBuffer);
        interfaceNameBuffer[bytes.Length] = 0;
    }
}
