using DotnetTun.Outbounds.Socks5;
using Xunit;

namespace DotnetTun.Core.Tests.Builder;

public sealed class Socks5OutboundOptionsTests
{
    [Fact]
    public void Constructor_WhenNameIsNotProvided_UsesDefaultName()
    {
        // Arrange / Act
        var options = new Socks5OutboundOptions("127.0.0.1", 7890);

        // Assert
        Assert.Equal("socks5", options.Name);
    }

    [Fact]
    public void Constructor_WithName_TrimsName()
    {
        // Arrange / Act
        var options = new Socks5OutboundOptions("127.0.0.1", 7890, name: " custom ");

        // Assert
        Assert.Equal("custom", options.Name);
    }

    [Fact]
    public void Constructor_WithEmptyHost_ThrowsArgumentException()
    {
        // Arrange / Act
        var act = () => new Socks5OutboundOptions(" ", 7890);

        // Assert
        Assert.Throws<ArgumentException>(act);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void Constructor_WithInvalidPort_ThrowsArgumentOutOfRangeException(int port)
    {
        // Arrange / Act
        var act = () => new Socks5OutboundOptions("127.0.0.1", port);

        // Assert
        Assert.Throws<ArgumentOutOfRangeException>(act);
    }

    [Fact]
    public void Constructor_WithEmptyName_ThrowsArgumentException()
    {
        // Arrange / Act
        var act = () => new Socks5OutboundOptions("127.0.0.1", 7890, name: " ");

        // Assert
        Assert.Throws<ArgumentException>(act);
    }
}
