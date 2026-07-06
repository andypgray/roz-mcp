using Microsoft.Extensions.Logging.Abstractions;
using Zphil.Roz.Services;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Services;

public class FixerCatalogTests(WorkspaceFixture fixture)
{
    [Fact]
    public async Task GetAsync_OnFixtureWithXunit_DiscoversXunitFixers()
    {
        // Arrange — TestFixture.Tests references xunit 2.x, which transitively pulls in
        // xunit.analyzers + xunit.analyzers.fixes containing CodeFixProviders for xUnit IDs.
        var catalog = new FixerCatalog(fixture.WorkspaceManager, NullLogger<FixerCatalog>.Instance);

        // Act
        IReadOnlyDictionary<string, FixerInfo> map = await catalog.GetAsync(TestContext.Current.CancellationToken);

        // Assert — xunit.analyzers.fixes ships fixers for many xUnit IDs.
        map.ShouldNotBeEmpty();
        map.Keys.ShouldContain(id => id.StartsWith("xUnit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetAsync_TwiceOnSameInstance_ReturnsCachedResult()
    {
        // Arrange
        var catalog = new FixerCatalog(fixture.WorkspaceManager, NullLogger<FixerCatalog>.Instance);

        // Act — discovery is reflection-heavy; second call must be served from cache.
        IReadOnlyDictionary<string, FixerInfo> first = await catalog.GetAsync(TestContext.Current.CancellationToken);
        IReadOnlyDictionary<string, FixerInfo> second = await catalog.GetAsync(TestContext.Current.CancellationToken);

        // Assert — same dictionary instance proves the cache short-circuit ran.
        second.ShouldBeSameAs(first);
    }

    [Fact]
    public async Task GetAsync_ResultEntries_HaveProviderTypeName()
    {
        // Arrange
        var catalog = new FixerCatalog(fixture.WorkspaceManager, NullLogger<FixerCatalog>.Instance);

        // Act
        IReadOnlyDictionary<string, FixerInfo> map = await catalog.GetAsync(TestContext.Current.CancellationToken);

        // Assert — every entry should have a non-empty provider type name.
        map.Values.ShouldAllBe(info => !String.IsNullOrEmpty(info.ProviderTypeName));
        map.Values.ShouldAllBe(info => !String.IsNullOrEmpty(info.DiagnosticId));
    }
}
