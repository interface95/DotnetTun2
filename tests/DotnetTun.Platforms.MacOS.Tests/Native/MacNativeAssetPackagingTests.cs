using System.Security.Cryptography;
using System.Xml.Linq;
using Xunit;

namespace DotnetTun.Platforms.MacOS.Tests.Native;

public sealed class MacNativeAssetPackagingTests
{
    [Theory]
    [InlineData("native/osx-arm64/libutunshim.dylib", "runtimes/osx-arm64/native/libutunshim.dylib")]
    [InlineData("native/osx-x64/libutunshim.dylib", "runtimes/osx-x64/native/libutunshim.dylib")]
    public void ProjectFile_IncludesLibutunshimNativeAssetForPack(string includePath, string packagePath)
    {
        // Arrange
        string projectFile = Path.Combine(FindRepositoryRoot(), "src", "DotnetTun.Platforms.MacOS", "DotnetTun.Platforms.MacOS.csproj");
        XDocument document = XDocument.Load(projectFile);

        // Act
        XElement? nativeAsset = document
            .Descendants("None")
            .SingleOrDefault(element => string.Equals((string?)element.Attribute("Include"), includePath, StringComparison.Ordinal));

        // Assert
        Assert.NotNull(nativeAsset);
        Assert.Equal("true", (string?)nativeAsset.Attribute("Pack"));
        Assert.Equal(packagePath, (string?)nativeAsset.Attribute("PackagePath"));
        Assert.Equal("false", (string?)nativeAsset.Attribute("Visible"));
    }

    [Theory]
    [InlineData("'$(RuntimeIdentifier)' == 'osx-arm64'", "native/osx-arm64/libutunshim.dylib")]
    [InlineData("'$(RuntimeIdentifier)' == 'osx-x64'", "native/osx-x64/libutunshim.dylib")]
    public void ProjectFile_CopiesRuntimeSpecificLibutunshimToOutputRoot(string condition, string includePath)
    {
        // Arrange
        string projectFile = Path.Combine(FindRepositoryRoot(), "src", "DotnetTun.Platforms.MacOS", "DotnetTun.Platforms.MacOS.csproj");
        XDocument document = XDocument.Load(projectFile);

        // Act
        XElement? copyItem = document
            .Descendants("ItemGroup")
            .Where(itemGroup => string.Equals((string?)itemGroup.Attribute("Condition"), condition, StringComparison.Ordinal))
            .Descendants("Content")
            .SingleOrDefault(element => string.Equals((string?)element.Attribute("Include"), includePath, StringComparison.Ordinal));

        // Assert
        Assert.NotNull(copyItem);
        Assert.Equal("false", (string?)copyItem.Attribute("Pack"));
        Assert.Equal("PreserveNewest", (string?)copyItem.Attribute("CopyToOutputDirectory"));
        Assert.Equal("libutunshim.dylib", (string?)copyItem.Attribute("TargetPath"));
        Assert.Equal("false", (string?)copyItem.Attribute("Visible"));
    }

    [Theory]
    [InlineData("native/osx-arm64/libutunshim.dylib")]
    [InlineData("native/osx-x64/libutunshim.dylib")]
    public void NativeSourceAsset_ExistsInRepositoryOwnedProjectDirectory(string relativeAssetPath)
    {
        // Arrange
        string assetPath = Path.Combine(FindRepositoryRoot(), "src", "DotnetTun.Platforms.MacOS", relativeAssetPath);

        // Act
        bool assetExists = File.Exists(assetPath);

        // Assert
        Assert.True(assetExists, $"Expected native asset to exist at {assetPath}.");
    }

    [Fact]
    public void NativeReadme_DocumentsLibutunshimProvenanceAndReviewPolicy()
    {
        // Arrange
        string readmePath = Path.Combine(FindRepositoryRoot(), "src", "DotnetTun.Platforms.MacOS", "native", "README.md");

        // Act
        string readme = File.ReadAllText(readmePath);

        // Assert
        Assert.Contains("## Source", readme, StringComparison.Ordinal);
        Assert.Contains("local upstream linker reference", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("during the migration", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("## Build", readme, StringComparison.Ordinal);
        Assert.Contains("current provenance", readme, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("## SHA-256", readme, StringComparison.Ordinal);
        Assert.Contains("native/osx-arm64/libutunshim.dylib", readme, StringComparison.Ordinal);
        Assert.Contains("native/osx-x64/libutunshim.dylib", readme, StringComparison.Ordinal);
        Assert.Contains("862aa238a467d7d808eda6f4265f3452d87665893c013e5a1bce56f15d16deee", readme, StringComparison.Ordinal);
        Assert.Contains("## Review policy", readme, StringComparison.Ordinal);
        Assert.Contains("review", readme, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("native/osx-arm64/libutunshim.dylib", "862aa238a467d7d808eda6f4265f3452d87665893c013e5a1bce56f15d16deee")]
    [InlineData("native/osx-x64/libutunshim.dylib", "862aa238a467d7d808eda6f4265f3452d87665893c013e5a1bce56f15d16deee")]
    public void NativeSourceAsset_HasExpectedSha256(string relativeAssetPath, string expectedSha256)
    {
        // Arrange
        string assetPath = Path.Combine(FindRepositoryRoot(), "src", "DotnetTun.Platforms.MacOS", relativeAssetPath);

        // Act
        using var stream = File.OpenRead(assetPath);
        byte[] hash = SHA256.HashData(stream);
        string actualSha256 = Convert.ToHexString(hash).ToLowerInvariant();

        // Assert
        Assert.Equal(expectedSha256, actualSha256);
    }

    [Fact]
    public void ProjectFile_DoesNotReferenceIgnoredLinkerDirectory()
    {
        // Arrange
        string projectFile = Path.Combine(FindRepositoryRoot(), "src", "DotnetTun.Platforms.MacOS", "DotnetTun.Platforms.MacOS.csproj");
        string projectText = File.ReadAllText(projectFile);

        // Act
        bool referencesLinkerDirectory = projectText.Contains("linker/", StringComparison.OrdinalIgnoreCase)
            || projectText.Contains("linker\\", StringComparison.OrdinalIgnoreCase);

        // Assert
        Assert.False(referencesLinkerDirectory);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DotnetTun.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
