using Zphil.Roz.Extensions;

namespace Zphil.Roz.Tests.Extensions;

public class IsFrameworkReferenceTests
{
    [Fact]
    public void IsFrameworkReference_NetCoreAppRef_ReturnsTrue()
    {
        const string Path = @"C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.0\ref\net10.0\System.Runtime.dll";

        ProjectExtensions.IsFrameworkReference(Path).ShouldBeTrue();
    }

    [Fact]
    public void IsFrameworkReference_AspNetCoreAppRef_ReturnsTrue()
    {
        const string Path = "/usr/share/dotnet/packs/Microsoft.AspNetCore.App.Ref/10.0.0/ref/net10.0/Microsoft.AspNetCore.dll";

        ProjectExtensions.IsFrameworkReference(Path).ShouldBeTrue();
    }

    [Fact]
    public void IsFrameworkReference_WindowsDesktopRef_ReturnsTrue()
    {
        const string Path = @"C:\Program Files\dotnet\packs\Microsoft.WindowsDesktop.App.Ref\10.0.0\ref\net10.0-windows\PresentationFramework.dll";

        ProjectExtensions.IsFrameworkReference(Path).ShouldBeTrue();
    }

    [Fact]
    public void IsFrameworkReference_NuGetPackagePath_ReturnsFalse()
    {
        const string Path = @"C:\Users\u\.nuget\packages\autofac\9.1.0\lib\net8.0\Autofac.dll";

        ProjectExtensions.IsFrameworkReference(Path).ShouldBeFalse();
    }

    [Fact]
    public void IsFrameworkReference_Null_ReturnsFalse() =>
        ProjectExtensions.IsFrameworkReference(null).ShouldBeFalse();

    [Fact]
    public void IsFrameworkReference_Empty_ReturnsFalse() =>
        ProjectExtensions.IsFrameworkReference("").ShouldBeFalse();
}
