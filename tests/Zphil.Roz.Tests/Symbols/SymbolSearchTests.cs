using Microsoft.CodeAnalysis;
using Zphil.Roz.Symbols;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Symbols;

public class SymbolSearchTests(WorkspaceFixture fixture)
{
    [Theory]
    [InlineData("System.Threading.Timer", "System.Threading")]
    [InlineData("System.Timers.Timer", "System.Timers")]
    public async Task FindMetadataTypes_AmbiguousBclTypeName_NamespaceQualified_ResolvesExactType(
        string metadataName, string expectedNamespace)
    {
        // Arrange — "Timer" exists in both System.Threading and System.Timers; a bare name would
        // resolve to an arbitrary first match. A namespace-qualified name must pin the exact type.
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);

        // Act
        List<INamedTypeSymbol> types = await SymbolSearch.FindMetadataTypesAsync(
            solution.Projects, metadataName, TestContext.Current.CancellationToken);

        // Assert — exactly one type, resolved from the requested namespace
        types.Count.ShouldBe(1);
        types[0].Name.ShouldBe("Timer");
        types[0].ContainingNamespace.ToDisplayString().ShouldBe(expectedNamespace);
    }
}
