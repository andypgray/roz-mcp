using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.UsingDirectiveToolTests;

public class BlankLineCorruptionTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task AddUsings_InsertsAtCorrectSortedPosition_PreservesExistingTrivia()
    {
        // Arrange — ShapeService.cs has one non-System using; add System usings to create two groups
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        // Pre-add two usings so the file has multiple consecutive usings
        await RewriteFileAsync(Fixture, serviceFile,
            c => "using System.ComponentModel;\r\nusing System.ComponentModel.DataAnnotations;\r\n" + c);

        string beforeContent = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);

        // Act — add one more using
        string result = await directiveTools.AddUsings(serviceFile, ["System.Text.Json"], ct: TestContext.Current.CancellationToken);

        // Assert — new using added, existing usings unchanged
        result.ShouldContain("Added 1 using(s)");
        string afterContent = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);

        // All original lines preserved
        foreach (string line in SplitLines(beforeContent))
        {
            SplitLines(afterContent).ShouldContain(line);
        }

        // New using appears in sorted position among System usings
        afterContent.ShouldContain("using System.Text.Json;");
        int textJsonIdx = afterContent.IndexOf("using System.Text.Json;");
        int dataAnnotationsIdx = afterContent.IndexOf("using System.ComponentModel.DataAnnotations;");
        textJsonIdx.ShouldBeGreaterThan(dataAnnotationsIdx,
            "System.Text.Json should sort after System.ComponentModel.DataAnnotations");
    }

    [Fact]
    public async Task RemoveUnusedUsings_DoesNotInsertBlankLinesBetweenRemainingUsings()
    {
        // Arrange — add multiple usings (one unused) to create a block of consecutive usings
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        await RewriteFileAsync(Fixture, serviceFile,
            c => "using System.ComponentModel;\r\nusing System.IO;\r\n" + c);

        // Act — remove unused System.IO and System.ComponentModel
        await directiveTools.RemoveUnusedUsings([serviceFile], ct: TestContext.Current.CancellationToken);

        // Assert — remaining usings should be consecutive (no blank lines between them)
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldHaveCorrectUsingGroupSeparation();
    }

    [Fact]
    public async Task AddUsings_ToFileWithMultipleExistingUsings_OnlyNewUsingInDiff()
    {
        // Arrange — start with 3 consecutive usings
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        await RewriteFileAsync(Fixture, serviceFile,
            c => "using System.ComponentModel;\r\nusing System.ComponentModel.DataAnnotations;\r\n" + c);

        string beforeContent = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);

        // Act — add one using
        await directiveTools.AddUsings(serviceFile, ["System.Text.Json"], ct: TestContext.Current.CancellationToken);

        string afterContent = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);

        // Assert — the only difference should be the added using line
        string[] beforeLines = SplitLines(beforeContent);
        string[] afterLines = SplitLines(afterContent);

        // All original usings should still be present with no extra whitespace changes
        foreach (string line in beforeLines.Where(l => l.TrimStart().StartsWith("using ")))
        {
            afterLines.ShouldContain(line);
        }

        // After should have exactly one more using line than before
        int beforeUsingCount = beforeLines.Count(l => l.TrimStart().StartsWith("using "));
        int afterUsingCount = afterLines.Count(l => l.TrimStart().StartsWith("using "));
        afterUsingCount.ShouldBe(beforeUsingCount + 1);
    }

    [Fact]
    public async Task RemoveUnusedUsings_MultipleRemaining_CorrectGroupSeparation()
    {
        // Arrange — 4 usings: 1 unused + 3 used (after removal, 3 remain)
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        // Add System.Linq (used by GetLargest's MaxBy), System.IO (unused), System.ComponentModel (unused)
        await RewriteFileAsync(Fixture, serviceFile,
            c => "using System.ComponentModel;\r\nusing System.IO;\r\nusing System.Linq;\r\n" + c);

        // Act
        await directiveTools.RemoveUnusedUsings([serviceFile], ct: TestContext.Current.CancellationToken);

        // Assert — remaining usings should be consecutive
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldHaveCorrectUsingGroupSeparation();
    }

    [Fact]
    public async Task RemoveUnusedUsings_SortOnly_CorrectGroupSeparation()
    {
        // Arrange — rewrite with two non-implicit, used usings in wrong order
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        await RewriteFileAsync(Fixture, serviceFile, _ => ShapeServiceWithDiagnostics());

        // Act — no unused usings, but sort is needed
        await directiveTools.RemoveUnusedUsings([serviceFile], ct: TestContext.Current.CancellationToken);

        // Assert — blank lines only between groups after sort
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldHaveCorrectUsingGroupSeparation();
    }
}
