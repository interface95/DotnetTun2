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

    private sealed class FailingUtunNativeApi : IUtunNativeApi
    {
        public int OpenUtun(int unit, Span<byte> interfaceNameBuffer, out int errorNumber)
        {
            errorNumber = 42;
            return -1;
        }
    }
}
