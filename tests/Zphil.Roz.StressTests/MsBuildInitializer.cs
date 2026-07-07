using System.Runtime.CompilerServices;
using Microsoft.Build.Locator;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Ensures MSBuild is registered once before any stress-test code runs.
///     Routes through <see cref="MsBuildBootstrap" /> so tests exercise the same
///     stable-version selection logic as production.
/// </summary>
internal static class MsBuildInitializer
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MsBuildBootstrap.Initialize();
        }

        FixtureRestorer.EnsureRestored(
            Path.GetDirectoryName(typeof(MsBuildInitializer).Assembly.Location)!);
    }
}
