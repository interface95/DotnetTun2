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
}
