using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Error handling and edge cases on nopCommerce's messy, large-scale codebase.
///     God classes (~2500 lines), dense using blocks, and legacy patterns amplify
///     robustness risks that smaller solutions don't expose.
/// </summary>
[Trait("Category", "Stress")]
public class NopRobustnessTests(NopTempWorkspaceFixture fixture, ITestOutputHelper output) : IClassFixture<NopTempWorkspaceFixture>
{
    [Fact]
    public async Task MalformedCSharp_ReplaceSymbol_InGodClass_FailsCleanly()
    {
        // Arrange — ProductService.cs is ~2500 lines
        CodeEditTools codeEdit = NopTestFileHelper.CreateEditTools(fixture);
        string productServiceFile = NopTestFileHelper.ProductServiceFile(fixture.WorkspaceManager);
        string originalContent = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);

        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(productServiceFile, "GetProductByIdAsync");

        // Act & Assert — unbalanced braces should be rejected
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => codeEdit.ReplaceSymbol(productServiceFile, "GetProductByIdAsync",
            "public virtual async Task<Product?> GetProductByIdAsync(int productId)\n    {\n        return await _productRepository.GetByIdAsync(productId, default);\n",
            line, column));

        ex.Message.ShouldContain("parse");

        // File should be unchanged
        string afterContent = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        afterContent.ShouldBe(originalContent);
    }

    [Fact]
    public async Task NonExistentSymbol_InGodClass_ThrowsCleanly()
    {
        // Arrange — scanning a 2500-line god class for a non-existent symbol should fail fast
        CodeEditTools codeEdit = NopTestFileHelper.CreateEditTools(fixture);
        string productServiceFile = NopTestFileHelper.ProductServiceFile(fixture.WorkspaceManager);

        // Act & Assert
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => codeEdit.ReplaceSymbol(productServiceFile, "NoSuchMethodEver", "void NoSuchMethodEver() {}"));

        ex.Message.ShouldContain("not found");
        cts.Token.IsCancellationRequested.ShouldBeFalse("Should fail fast, not hang");
    }

    [Fact]
    public async Task RapidAddUsings_50Usings_NopFile_DeduplicatedAndSorted()
    {
        // Arrange — Product.cs already has nop-specific usings (Nop.Core.Domain.*, etc.)
        UsingDirectiveTools usingsDirective = NopTestFileHelper.CreateUsingTools(fixture);
        string productFile = NopTestFileHelper.ProductFile(fixture.WorkspaceManager);

        // Act — add 50 usings one at a time, mixing with existing nop-convention usings
        await TimingHelper.TimeAsync("Nop_RapidAddUsings_50", async () =>
        {
            for (var i = 0; i < 50; i++)
            {
                await usingsDirective.AddUsings(productFile, [$"System.StressTest{i:D2}"]);
            }
        }, output);

        // Assert
        string content = await File.ReadAllTextAsync(productFile, TestContext.Current.CancellationToken);
        string[] lines = NopTestFileHelper.SplitLines(content);

        // All 50 usings should be present
        for (var i = 0; i < 50; i++)
        {
            content.ShouldContain($"using System.StressTest{i:D2};");
        }

        // Usings should be sorted
        List<string> usingLines = lines
            .Where(l => l.TrimStart().StartsWith("using "))
            .Select(l => l.Trim())
            .ToList();

        List<string> sorted = usingLines
            .OrderBy(u => u.StartsWith("using System") ? 0 : 1)
            .ThenBy(u => u)
            .ToList();
        usingLines.ShouldBe(sorted, "Usings should be in sorted order (System-first)");

        // No duplicates
        usingLines.Count.ShouldBe(usingLines.Distinct().Count(), "No duplicate usings should exist");
    }

    [Fact]
    public async Task RegexTimeout_InGodClass_DoesNotHang()
    {
        // Arrange — prepend a catastrophic backtracking trigger to ProductService (2500 lines)
        CodeEditTools codeEdit = NopTestFileHelper.CreateEditTools(fixture);
        string productServiceFile = NopTestFileHelper.ProductServiceFile(fixture.WorkspaceManager);

        await NopTestFileHelper.RewriteFileAsync(fixture, productServiceFile,
            content => "// " + new string('a', 30) + "!\n" + content);

        // Act & Assert — the regex has a 5-second timeout; the test itself should complete within 15s
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        // Per-op exceptions (regex timeout, no-match) are captured into the result string by the batch path,
        // but the 5s regex timeout still bounds wall-clock time.
        await codeEdit.ReplaceContent(
            [new ReplaceContentRequest(productServiceFile, @"(a+)+b", "replaced", true)], ct: cts.Token);

        cts.Token.IsCancellationRequested.ShouldBeFalse("Test should complete well within the 15s timeout");
    }

    [Fact]
    public async Task InsertSymbol_Before_InGodClass_PositionCorrect()
    {
        // Arrange — insert a member before GetProductByIdAsync in ProductService
        CodeEditTools codeEdit = NopTestFileHelper.CreateEditTools(fixture);
        NavigationTools nav = NopTestFileHelper.CreateNavigationTools(fixture);
        string productServiceFile = NopTestFileHelper.ProductServiceFile(fixture.WorkspaceManager);

        // Act — insert before a method in the 30+ member god class
        await codeEdit.InsertSymbol(productServiceFile, "GetProductByIdAsync", "public virtual int InsertedBeforeGetProduct() => 42;", InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert — inserted member should appear before target in file
        string content = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        string[] lines = NopTestFileHelper.SplitLines(content);

        int insertedLine = Array.FindIndex(lines, l => l.Contains("InsertedBeforeGetProduct"));
        int targetLine = Array.FindIndex(lines, l => l.Contains("GetProductByIdAsync") && !l.Contains("InsertedBefore"));
        insertedLine.ShouldBeGreaterThanOrEqualTo(0, "Inserted member should be in the file");
        targetLine.ShouldBeGreaterThan(0, "Target method should be in the file");
        insertedLine.ShouldBeLessThan(targetLine, "Inserted member should appear before GetProductByIdAsync");

        // File should still parse
        string overview = await nav.GetSymbolsOverview([productServiceFile], ct: TestContext.Current.CancellationToken);
        overview.ShouldContain("InsertedBeforeGetProduct");
        overview.ShouldContain("GetProductByIdAsync");
    }

    [Fact]
    public async Task NonExistentFile_ReplaceContent_ReportsErrorPerOp()
    {
        // Arrange
        CodeEditTools codeEdit = NopTestFileHelper.CreateEditTools(fixture);
        string fakePath = Path.Combine(fixture.WorkspaceManager.SolutionDirectory!, "DoesNotExist.cs");

        // Act
        string result = await codeEdit.ReplaceContent([new ReplaceContentRequest(fakePath, "anything", "replacement")], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Error");
        result.ShouldContain("File not found");
    }

    [Fact]
    public async Task NonExistentFile_InsertSymbol_ThrowsCleanly()
    {
        // Arrange
        CodeEditTools codeEdit = NopTestFileHelper.CreateEditTools(fixture);
        string fakePath = Path.Combine(fixture.WorkspaceManager.SolutionDirectory!, "DoesNotExist.cs");

        // Act & Assert
        await Should.ThrowAsync<Exception>(() => codeEdit.InsertSymbol(fakePath, "Anything", "public void Foo() {}"));
    }

    [Fact]
    public async Task AddDuplicateUsings_Idempotent()
    {
        // Arrange
        UsingDirectiveTools usingsDirective = NopTestFileHelper.CreateUsingTools(fixture);
        string productFile = NopTestFileHelper.ProductFile(fixture.WorkspaceManager);

        // Act — add the same using 10 times
        for (var i = 0; i < 10; i++)
        {
            await usingsDirective.AddUsings(productFile, ["System.Text"], ct: TestContext.Current.CancellationToken);
        }

        // Assert
        string content = await File.ReadAllTextAsync(productFile, TestContext.Current.CancellationToken);
        int count = content.Split("using System.Text;").Length - 1;
        count.ShouldBe(1, "Should have exactly one 'using System.Text;'");
    }
}
