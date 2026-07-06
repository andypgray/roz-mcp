using System.Text.RegularExpressions;
using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.Presentation;
using Zphil.Roz.Services;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Presentation;

/// <summary>
///     Pins the per-member line-number suffix added to depth&gt;0 symbol listings: a bare <c>:line</c> for
///     single-file types and the file-scoped overview path, a <c>{file}:line</c> form for a partial type
///     shown across files (find_symbol/go_to_definition), and no suffix at all for metadata (BCL/NuGet)
///     members so those listings stay byte-identical.
/// </summary>
public class MemberLineListingTests(WorkspaceFixture fixture)
{
    private readonly NavigationService navigationService = TestFileHelper.CreateNavigationService(fixture);

    [Fact]
    public async Task GetSymbolsOverview_MemberRows_CarryBareLineNumber()
    {
        // Arrange — Shape.cs declares Area (line 7), Perimeter (10), Describe (13) in a single file.
        SymbolsOverviewResult result = await navigationService.GetSymbolsOverviewAsync(
            "TestFixture/Shapes/Shape.cs", ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert — overview member rows carry the bare ":line" (members are already scoped to one file)
        output.ShouldContain("double Area  :7");
        output.ShouldContain("string Describe()  :13");
    }

    [Fact]
    public async Task FindSymbol_SingleFileType_MemberRowsCarryBareLineNumber()
    {
        // Arrange — IShape is declared in one file: Area (9), Perimeter (12), Describe (18).
        FindSymbolResult result = await navigationService.FindSymbolAsync(
            "IShape", depth: 1, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);

        // Assert
        output.ShouldContain("double Area  :9");
        output.ShouldContain("double Perimeter  :12");
        output.ShouldContain("string Describe()  :18");
    }

    [Fact]
    public async Task FindSymbol_PartialTypeAcrossFiles_MemberRowsCarryFileName()
    {
        // Arrange — PartialShapeProcessor spans TypeKindExamples.cs (ProcessName, ProcessCount) and
        // PartialShapeProcessor.Extra.cs (Reset). find_symbol applies no file filter, so each member row
        // names its own file to stay unambiguous across the partials.
        FindSymbolResult result = await navigationService.FindSymbolAsync(
            "PartialShapeProcessor", depth: 1, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);
        string[] memberRows = output.Split('\n').Where(l => l.TrimStart().StartsWith('[')).ToArray();

        // Assert — the file name disambiguates rows drawn from different partials
        memberRows.ShouldContain(l => l.Contains("ProcessName") && l.Contains("TypeKindExamples.cs:"));
        memberRows.ShouldContain(l => l.Contains("ProcessCount") && l.Contains("TypeKindExamples.cs:"));
        memberRows.ShouldContain(l => l.Contains("Reset") && l.Contains("PartialShapeProcessor.Extra.cs:"));
    }

    [Fact]
    public async Task FindSymbol_MetadataType_MemberRowsHaveNoLineSuffix()
    {
        // Arrange — a BCL type resolved by FQN has no source, so its members carry no line suffix
        // (BCL/NuGet member listings must stay byte-identical to before the suffix was added).
        // System.IDisposable is non-generic, so it resolves by plain metadata name; its one member
        // (Dispose) exercises the metadata → no-suffix branch.
        FindSymbolResult result = await navigationService.FindSymbolAsync(
            "System.IDisposable", depth: 1, ct: TestContext.Current.CancellationToken);

        // Act
        string output = NavigationResultFormatter.Format(result);
        string[] memberRows = output.Split('\n').Where(l => l.TrimStart().StartsWith('[')).ToArray();

        // Assert — members render, but none carry the "  :line" suffix that source members get
        memberRows.ShouldNotBeEmpty();
        foreach (string row in memberRows)
        {
            Regex.IsMatch(row, @"  :\d")
                .ShouldBeFalse($"Metadata member row unexpectedly carried a line suffix: {row}");
        }
    }
}
