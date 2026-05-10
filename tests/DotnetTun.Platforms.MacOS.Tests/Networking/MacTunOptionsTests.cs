using System.Net;
using DotnetTun.Platforms.MacOS.Networking;
using Xunit;

namespace DotnetTun.Platforms.MacOS.Tests.Networking;

public sealed class MacTunOptionsTests
{
    [Fact]
    public void Constructor_WithEmptyInterfaceName_ThrowsArgumentException()
    {
        // Arrange / Act
        var act = () => new MacTunOptions(" ", IPAddress.Parse("10.88.0.2"), IPAddress.Parse("10.88.0.1"), 1420, "198.18.0.0/15", []);

        // Assert
        Assert.Throws<ArgumentException>(act);
    }

    [Fact]
    public void Constructor_WithInvalidMtu_ThrowsArgumentOutOfRangeException()
    {
        // Arrange / Act
        var act = () => new MacTunOptions("utun9", IPAddress.Parse("10.88.0.2"), IPAddress.Parse("10.88.0.1"), 0, "198.18.0.0/15", []);

        // Assert
        Assert.Throws<ArgumentOutOfRangeException>(act);
    }

    [Theory]
    [InlineData("utun9; touch /tmp/pwned")]
    [InlineData("en0")]
    [InlineData("utun/9")]
    public void Constructor_WithUnsafeInterfaceName_ThrowsArgumentException(string interfaceName)
    {
        // Arrange / Act
        var act = () => new MacTunOptions(interfaceName, IPAddress.Parse("10.88.0.2"), IPAddress.Parse("10.88.0.1"), 1420, "198.18.0.0/15", []);

        // Assert
        Assert.Throws<ArgumentException>(act);
    }

    [Theory]
    [InlineData("198.18.0.0/15; touch /tmp/pwned")]
    [InlineData("198.18.0.0")]
    [InlineData("198.18.0.0/99")]
    public void Constructor_WithInvalidFakeIpCidr_ThrowsArgumentException(string fakeIpCidr)
    {
        // Arrange / Act
        var act = () => new MacTunOptions("utun9", IPAddress.Parse("10.88.0.2"), IPAddress.Parse("10.88.0.1"), 1420, fakeIpCidr, []);

        // Assert
        Assert.Throws<ArgumentException>(act);
    }
}
