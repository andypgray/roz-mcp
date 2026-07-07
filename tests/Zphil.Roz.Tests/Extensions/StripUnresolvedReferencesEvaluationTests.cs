using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Rename;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Extensions;

/// <summary>
///     Pinning tests for the Roslyn cross-project crash on <see cref="UnresolvedAnalyzerReference" />
///     that motivated commit 6749023. They bypass <see cref="Zphil.Roz.Infrastructure.WorkspaceManager" />
///     (which strips via <see cref="Zphil.Roz.Extensions.SolutionExtensions.StripUnresolvedReferences" />)
///     so the trigger reaches the API.
/// </summary>
/// <remarks>
///     <para>
///         <b>Inverted assertions:</b> three tests assert the bug <i>still throws</i>, so they go
///         green while the workaround is load-bearing. When a future Roslyn upgrade fixes
///         <c>SerializerService.CreateChecksum(AnalyzerReference)</c>, those tests turn red and
///         the failure message tells the next maintainer what to do.
///         <see cref="RenameSymbolAsync_WithUnresolvedAnalyzerRef_DoesNotCrash" /> is not inverted —
///         <see cref="Renamer.RenameSymbolAsync" /> doesn't traverse the buggy path in 5.3, so it
///         asserts no-crash directly; if a future Roslyn regresses it, the test fails naturally.
///     </para>
///     <para>
///         Only the analyzer-injection variant is exercised. The original commit singled out
///         unresolved analyzer refs as the crash trigger and called the metadata strip "defensive";
///         an <see cref="UnresolvedMetadataReference" /> cannot be synthesized from a test (direct
///         <c>solution.AddMetadataReference</c> is rejected by <c>Compilation.ValidateReferences</c>;
///         a csproj <c>&lt;Reference&gt;</c> with a missing <c>HintPath</c> is dropped by MSBuild
///         before reaching the workspace).
///     </para>
/// </remarks>
public sealed class StripUnresolvedReferencesEvaluationTests
{
    /// <summary>
    ///     Message shown when a Should.ThrowAsync assertion fails — i.e. the API
    ///     stopped throwing, meaning Roslyn likely fixed the underlying bug.
    /// </summary>
    private static string CleanupHint(string apiName) =>
        $"{apiName} no longer throws InvalidOperationException on UnresolvedAnalyzerReference. " +
        "The Roslyn bug in SerializerService.CreateChecksum(AnalyzerReference) appears to be fixed. " +
        "If the other tests in this class also pass, the workaround is no longer load-bearing: " +
        "delete StripUnresolvedReferences (Extensions/SolutionExtensions.cs) and the snapshot/restore " +
        "block in WorkspaceManager.LoadSolutionInternalAsync (Infrastructure/WorkspaceManager.cs, " +
        "around the StripUnresolvedReferences call). Verify by re-running the cross-project stress " +
        "tests with the strip removed.";

    [Fact]
    public async Task FindImplementationsAsync_WithUnresolvedAnalyzerRef_StillThrows()
    {
        await using SyntheticUnresolvedWorkspace synth = await SyntheticUnresolvedWorkspace.CreateAsync(TestContext.Current.CancellationToken);

        INamedTypeSymbol iShape = await synth.GetTypeAsync("TestFixture.Shapes.IShape", TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(
            () => SymbolFinder.FindImplementationsAsync(iShape, synth.Solution),
            CleanupHint("SymbolFinder.FindImplementationsAsync"));
    }

    [Fact]
    public async Task FindDerivedClassesAsync_WithUnresolvedAnalyzerRef_StillThrows()
    {
        await using SyntheticUnresolvedWorkspace synth = await SyntheticUnresolvedWorkspace.CreateAsync(TestContext.Current.CancellationToken);

        INamedTypeSymbol shape = await synth.GetTypeAsync("TestFixture.Shapes.Shape", TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(
            () => SymbolFinder.FindDerivedClassesAsync(shape, synth.Solution),
            CleanupHint("SymbolFinder.FindDerivedClassesAsync"));
    }

    [Fact]
    public async Task FindOverridesAsync_WithUnresolvedAnalyzerRef_StillThrows()
    {
        await using SyntheticUnresolvedWorkspace synth = await SyntheticUnresolvedWorkspace.CreateAsync(TestContext.Current.CancellationToken);

        INamedTypeSymbol shape = await synth.GetTypeAsync("TestFixture.Shapes.Shape", TestContext.Current.CancellationToken);
        ISymbol describe = shape.GetMembers("Describe").Single();

        await Should.ThrowAsync<InvalidOperationException>(
            () => SymbolFinder.FindOverridesAsync(describe, synth.Solution),
            CleanupHint("SymbolFinder.FindOverridesAsync"));
    }

    [Fact]
    public async Task RenameSymbolAsync_WithUnresolvedAnalyzerRef_DoesNotCrash()
    {
        await using SyntheticUnresolvedWorkspace synth = await SyntheticUnresolvedWorkspace.CreateAsync(TestContext.Current.CancellationToken);

        INamedTypeSymbol iShape = await synth.GetTypeAsync("TestFixture.Shapes.IShape", TestContext.Current.CancellationToken);

        Solution renamed = await Renamer.RenameSymbolAsync(synth.Solution, iShape, new SymbolRenameOptions(), "IShapeRenamed", TestContext.Current.CancellationToken);

        renamed.ShouldNotBeSameAs(synth.Solution);
    }

    private sealed class SyntheticUnresolvedWorkspace(
        MSBuildWorkspace workspace,
        Solution solution,
        string tempDirectory) : IAsyncDisposable
    {
        public Solution Solution { get; } = solution;

        public ValueTask DisposeAsync()
        {
            workspace.Dispose();
            Directory.Delete(tempDirectory, true);
            return ValueTask.CompletedTask;
        }

        public static async Task<SyntheticUnresolvedWorkspace> CreateAsync(CancellationToken ct)
        {
            string sourceDir = Path.GetDirectoryName(WorkspaceFixture.FixtureSolutionPath)!;
            string tempDir = Path.Combine(Path.GetTempPath(), "roslyn-strip-eval", Guid.NewGuid().ToString("N"));
            TestFileHelper.CopyDirectory(sourceDir, tempDir);

            InjectAnalyzerIntoCsproj(tempDir);

            var ws = MSBuildWorkspace.Create();
            try
            {
                string slnPath = Path.Combine(tempDir, "TestFixture.sln");
                Solution solution = await ws.OpenSolutionAsync(slnPath, cancellationToken: ct);

                AssertHasUnresolvedAnalyzerRef(solution);

                return new SyntheticUnresolvedWorkspace(ws, solution, tempDir);
            }
            catch
            {
                ws.Dispose();
                Directory.Delete(tempDir, true);
                throw;
            }
        }

        public async Task<INamedTypeSymbol> GetTypeAsync(string fullyQualifiedMetadataName, CancellationToken ct)
        {
            foreach (Project project in Solution.Projects)
            {
                Compilation? compilation = await project.GetCompilationAsync(ct);
                INamedTypeSymbol? symbol = compilation?.GetTypeByMetadataName(fullyQualifiedMetadataName);
                if (symbol is not null && symbol.Locations.Any(l => l.IsInSource))
                {
                    return symbol;
                }
            }

            throw new InvalidOperationException($"Could not find {fullyQualifiedMetadataName} in source projects.");
        }

        private static void InjectAnalyzerIntoCsproj(string tempDir)
        {
            string csprojPath = Path.Combine(tempDir, "TestFixture", "TestFixture.csproj");
            string xml = File.ReadAllText(csprojPath);

            string modifiedXml = xml.Replace(
                "</Project>",
                """

                    <ItemGroup>
                        <Analyzer Include="NonExistent.Analyzer.dll" />
                    </ItemGroup>

                </Project>
                """);

            File.WriteAllText(csprojPath, modifiedXml);
        }

        private static void AssertHasUnresolvedAnalyzerRef(Solution solution)
        {
            int count = solution.Projects.Sum(p =>
                p.AnalyzerReferences.OfType<UnresolvedAnalyzerReference>().Count());

            if (count == 0)
            {
                throw new InvalidOperationException(
                    "Test setup broken: expected at least one UnresolvedAnalyzerReference but found none. " +
                    "MSBuild may have stopped emitting unresolved analyzer refs into the workspace.");
            }
        }
    }
}
