using DotnetTun.Platforms.MacOS.Networking;
using Xunit;

namespace DotnetTun.Platforms.MacOS.Tests.Networking;

public sealed class MacCommandTests
{
    [Fact]
    public void Constructor_CopiesArgumentsSoCallerMutationDoesNotChangeCommandState()
    {
        // Arrange
        List<string> arguments = ["route", "delete", "-net", "198.18.0.0/15"];
        var command = new MacCommand("sudo", arguments, IgnoreFailure: true);
        var equivalent = new MacCommand("sudo", ["route", "delete", "-net", "198.18.0.0/15"], IgnoreFailure: true);
        int hashCode = command.GetHashCode();
        string rendered = command.ToString();

        // Act
        arguments[0] = "ifconfig";
        arguments.Add("2>/dev/null || true");

        // Assert
        Assert.Equal(["route", "delete", "-net", "198.18.0.0/15"], command.Arguments);
        Assert.Equal(equivalent, command);
        Assert.Equal(hashCode, command.GetHashCode());
        Assert.Equal("sudo route delete -net 198.18.0.0/15", rendered);
        Assert.Equal(rendered, command.ToString());
    }
}
