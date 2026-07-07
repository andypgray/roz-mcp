using Zphil.Roz.Services;

namespace Zphil.Roz.Tests.Services;

public class NuGetPathExtractorTests
{
    [Fact]
    public void TryGetPackageId_StandardLinuxNuGetPath_ReturnsId()
    {
        // Arrange
        const string Path = "/home/user/.nuget/packages/newtonsoft.json/13.0.1/lib/net6.0/Newtonsoft.Json.dll";

        // Act
        string? id = NuGetPathExtractor.TryGetPackageId(Path);

        // Assert
        id.ShouldBe("newtonsoft.json");
    }

    [Fact]
    public void TryGetPackageId_StandardWindowsNuGetPath_ReturnsId()
    {
        // Arrange
        const string Path = @"C:\Users\u\.nuget\packages\autofac\9.1.0\lib\net8.0\Autofac.dll";

        // Act
        string? id = NuGetPathExtractor.TryGetPackageId(Path);

        // Assert
        id.ShouldBe("autofac");
    }

    [Fact]
    public void TryGetPackageId_FrameworkRefPath_ReturnsNull()
    {
        // Arrange — SDK shared-framework reference packs live under {dotnet}/packs/, not .nuget/packages.
        const string Path = @"C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.0\ref\net10.0\System.Runtime.dll";

        // Act
        string? id = NuGetPathExtractor.TryGetPackageId(Path);

        // Assert
        id.ShouldBeNull();
    }

    [Fact]
    public void TryGetPackageId_DirectDllReference_ReturnsNull()
    {
        // Arrange
        const string Path = @"C:\Lib\Foo.dll";

        // Act
        string? id = NuGetPathExtractor.TryGetPackageId(Path);

        // Assert
        id.ShouldBeNull();
    }

    [Fact]
    public void TryGetPackageId_RelocatedNuGetCache_ReturnsId()
    {
        // Arrange — non-standard NUGET_PACKAGES root, but the .nuget/packages segment pair is the marker.
        const string Path = @"D:\caches\.nuget\packages\serilog\3.1.1\lib\net8.0\Serilog.dll";

        // Act
        string? id = NuGetPathExtractor.TryGetPackageId(Path);

        // Assert
        id.ShouldBe("serilog");
    }

    [Fact]
    public void TryGetPackageId_CaseInsensitive_ReturnsLowercaseId()
    {
        // Arrange — NuGet normalizes folder names to lowercase, but match defensively against odd casings.
        const string Path = @"C:\Users\u\.NuGet\Packages\Autofac\9.1.0\lib\net8.0\Autofac.dll";

        // Act
        string? id = NuGetPathExtractor.TryGetPackageId(Path);

        // Assert
        id.ShouldBe("autofac");
    }

    [Fact]
    public void TryGetPackageId_NullOrEmptyPath_ReturnsNull()
    {
        NuGetPathExtractor.TryGetPackageId(null).ShouldBeNull();
        NuGetPathExtractor.TryGetPackageId("").ShouldBeNull();
    }
}
