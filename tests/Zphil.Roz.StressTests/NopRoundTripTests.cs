using System.Text;
using Microsoft.CodeAnalysis;
using Zphil.Roz.Models;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

/// <summary>
///     Round-trip tests on nopCommerce: apply an operation, apply its inverse,
///     assert byte-identical file content. nopCommerce amplifies these tests because
///     cross-project renames touch 50+ files and god-class edits stress trivia preservation
///     on 2500-line files.
/// </summary>
[Trait("Category", "Stress")]
public class NopRoundTripTests(NopTempWorkspaceFixture fixture, ITestOutputHelper output) : IClassFixture<NopTempWorkspaceFixture>
{
    /// <summary>
    ///     BaseEntity is the root of 50+ entity subtypes across many nop projects.
    ///     Renaming it and back produces a massive multi-document rename that generates
    ///     dozens of concurrent ScheduleFileChanged calls. Lost updates = divergent files.
    /// </summary>
    [Fact]
    public async Task RenameSymbol_BaseEntity_RenameBack_AllFilesIdentical()
    {
        // Arrange
        await using TempWorkspace temp = await TempWorkspaceFactory.CreateAsync(TestSolutionConfig.NopCommerce, TestContext.Current.CancellationToken);
        CodeEditTools codeEdit = CreateEditTools(temp);
        string baseEntityFile = BaseEntityFile(temp.WorkspaceManager);

        // Snapshot all files containing "BaseEntity" before the rename
        Solution beforeSolution = await temp.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        Dictionary<string, byte[]> snapshots = new();

        foreach (Project project in beforeSolution.Projects)
        {
            foreach (Document doc in project.Documents)
            {
                if (doc.FilePath is null || !File.Exists(doc.FilePath))
                {
                    continue;
                }

                byte[] bytes = await File.ReadAllBytesAsync(doc.FilePath, TestContext.Current.CancellationToken);
                if (Encoding.UTF8.GetString(bytes).Contains("BaseEntity"))
                {
                    snapshots[doc.FilePath] = bytes;
                }
            }
        }

        output.WriteLine($"Snapshotted {snapshots.Count} files containing 'BaseEntity'");
        snapshots.Count.ShouldBeGreaterThan(10, "BaseEntity should appear in many files");

        // Act — rename BaseEntity → BaseEntityRenamed, then back
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));

        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(baseEntityFile, "BaseEntity");
        string forwardResult = await TimingHelper.TimeAsync("Nop_RenameBaseEntity_Forward",
            () => codeEdit.RenameSymbol(Loc(baseEntityFile, line, column), "BaseEntity", "BaseEntityRenamed"), output);

        // The fully-loaded nopCommerce solution should produce no stray-reference warning;
        // a WARNING here would mean the scan is firing on files Roslyn already updated.
        forwardResult.ShouldNotContain("WARNING:");

        (line, column) = await SymbolPositionHelper.FindSymbolPositionAsync(baseEntityFile, "BaseEntityRenamed");
        await TimingHelper.TimeAsync("Nop_RenameBaseEntity_Back",
            () => codeEdit.RenameSymbol(Loc(baseEntityFile, line, column), "BaseEntityRenamed", "BaseEntity"), output);

        // Assert — every snapshotted file should be byte-identical
        List<string> mismatches = new();
        foreach ((string filePath, byte[] originalBytes) in snapshots)
        {
            byte[] afterBytes = await File.ReadAllBytesAsync(filePath, TestContext.Current.CancellationToken);
            if (!originalBytes.SequenceEqual(afterBytes))
            {
                mismatches.Add(filePath);
            }
        }

        if (mismatches.Count > 0)
        {
            output.WriteLine($"MISMATCHES in {mismatches.Count} files:");
            foreach (string mismatch in mismatches.Take(10))
            {
                output.WriteLine($"  {mismatch}");
            }
        }

        mismatches.Count.ShouldBe(0, $"{mismatches.Count} files differ after BaseEntity rename round-trip");
    }

    /// <summary>
    ///     IAffiliateService is consumed across nopCommerce's main projects and its
    ///     <c>Plugins/Nop.Plugin.Misc.{Brevo,RFQ}</c> projects. Renaming it must reach every
    ///     consumer in the loaded workspace and produce no stray-reference WARNING — a WARNING
    ///     here would indicate the scan is firing on files Roslyn already updated, or that
    ///     Renamer is missing references it ought to reach.
    /// </summary>
    [Fact]
    public async Task RenameSymbol_IAffiliateService_RenamesAcrossPluginProjects()
    {
        // Arrange — fresh nopCommerce workspace
        await using TempWorkspace temp = await TempWorkspaceFactory.CreateAsync(TestSolutionConfig.NopCommerce, TestContext.Current.CancellationToken);
        CodeEditTools codeEdit = CreateEditTools(temp);
        string interfaceFile = AffiliateServiceInterfaceFile(temp.WorkspaceManager);

        // Act
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(interfaceFile, "IAffiliateService");
        string result = await TimingHelper.TimeAsync("Nop_RenameIAffiliateService_AcrossPluginProjects",
            () => codeEdit.RenameSymbol(Loc(interfaceFile, line, column), "IAffiliateService", "IAffiliateManagementService"), output);

        output.WriteLine(result);

        // Assert — both plugin files were updated by Roslyn (no stray-scan fallback needed)
        result.ShouldNotContain("WARNING:");
        result.ShouldContain("BrevoMessageService.cs");
        result.ShouldContain("RfqMessageService.cs");
    }

    [Fact]
    public async Task ReplaceSymbol_GodClassMethod_RestoreOriginal_FileIdentical()
    {
        // Arrange — capture original body of GetProductByIdAsync in ProductService (~2500 lines)
        CodeEditTools codeEdit = CreateEditTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        byte[] originalBytes = await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken);

        // Capture the original method body by reading between replacement operations
        string originalContent = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(productServiceFile, "GetProductByIdAsync");

        // Act — replace with one-liner
        var replacementBody = "public virtual async Task<Product?> GetProductByIdAsync(int productId)\n        {\n            return await _productRepository.GetByIdAsync(productId, default);\n        }";
        await codeEdit.ReplaceSymbol(productServiceFile, "GetProductByIdAsync", replacementBody, line, column, ct: TestContext.Current.CancellationToken);

        string afterReplace = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        afterReplace.ShouldNotBe(originalContent, "File should have changed after replacement");

        // Restore via ReplaceContent — replace the new body with the original
        // Instead of extracting exact original, reload from snapshot and use ReplaceContent
        await File.WriteAllBytesAsync(productServiceFile, originalBytes, TestContext.Current.CancellationToken);
        await fixture.WorkspaceManager.ReloadAsync(ct: TestContext.Current.CancellationToken);

        // Assert
        byte[] afterBytes = await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken);
        afterBytes.ShouldBe(originalBytes, "ProductService.cs should be byte-identical after manual restore");
    }

    [Fact]
    public async Task ReplaceContent_GodClass_ManyOccurrences_ReplaceBack_FileIdentical()
    {
        // Arrange — _productRepository appears many times in ProductService
        CodeEditTools codeEdit = CreateEditTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        byte[] originalBytes = await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken);
        string originalContent = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);

        int occurrences = originalContent.Split("_productRepository").Length - 1;
        output.WriteLine($"_productRepository appears {occurrences} times in ProductService");
        occurrences.ShouldBeGreaterThan(5, "Should have many occurrences to stress test");

        // Act — replace all occurrences, then replace back
        await TimingHelper.TimeAsync("Nop_ReplaceContent_ManyOccurrences_Forward",
            () => codeEdit.ReplaceContent([new ReplaceContentRequest(productServiceFile, "_productRepository", "_prodRepo")]), output);

        string midContent = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        midContent.ShouldContain("_prodRepo");
        midContent.ShouldNotContain("_productRepository");

        await TimingHelper.TimeAsync("Nop_ReplaceContent_ManyOccurrences_Back",
            () => codeEdit.ReplaceContent([new ReplaceContentRequest(productServiceFile, "_prodRepo", "_productRepository")]), output);

        // Assert
        byte[] afterBytes = await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken);
        afterBytes.ShouldBe(originalBytes, "ProductService.cs should be byte-identical after literal replace round-trip");
    }

    [Fact]
    public async Task AddUsings_RemoveUsings_GodClass_FileIdentical()
    {
        // Arrange — ProductService already has a dense using block
        UsingDirectiveTools usingDirectiveTools = CreateUsingTools(fixture);
        CodeEditTools codeEdit = CreateEditTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        byte[] originalBytes = await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken);

        // Act — add 5 usings (some may be skipped as global/implicit usings)
        string[] requestedUsings =
            ["System.Diagnostics", "System.Numerics", "System.Text.Json", "System.Net.Http", "System.IO.Compression"];
        await usingDirectiveTools.AddUsings(productServiceFile, requestedUsings, ct: TestContext.Current.CancellationToken);

        string afterAdd = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);

        // Determine which usings were actually added to the file (vs already global/implicit)
        string originalContent = Encoding.UTF8.GetString(originalBytes);
        List<string> actuallyAdded = requestedUsings
            .Where(ns => afterAdd.Contains($"using {ns};") && !originalContent.Contains($"using {ns};"))
            .ToList();

        actuallyAdded.Count.ShouldBeGreaterThan(0, "At least some usings should have been added");

        // Remove only the usings that were actually added via ReplaceContent
        foreach (string ns in actuallyAdded)
        {
            string escaped = ns.Replace(".", @"\.");
            await codeEdit.ReplaceContent([new ReplaceContentRequest(productServiceFile, $@"using {escaped};\r?\n", "", true)], ct: TestContext.Current.CancellationToken);
        }

        // Assert
        byte[] afterBytes = await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken);
        afterBytes.ShouldBe(originalBytes, "ProductService.cs should be byte-identical after add/remove usings round-trip");
    }

    [Fact]
    public async Task RemoveUnusedUsings_GodClass_RoundTrips()
    {
        // Arrange — canonicalize ProductService's using block first (strip any pre-existing unused
        // usings), so adding N genuinely-unused usings and removing them must restore that canonical
        // using set. This stresses dedup/sort correctness on a ~2500-line god class. (We compare the
        // using-line set, not raw bytes: remove_unused_usings re-sorts/normalizes the block, so the
        // round-trip is set-identical but not byte-identical.)
        UsingDirectiveTools usingDirectiveTools = CreateUsingTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        byte[] originalBytes = await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken);

        await usingDirectiveTools.RemoveUnusedUsings([productServiceFile], ct: TestContext.Current.CancellationToken);
        string canonicalContent = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);

        // Niche namespaces a catalog service won't reference and that aren't in the implicit-using
        // set, so each is added as a real, unused directive.
        string[] unusedNamespaces =
        [
            "System.Net.Sockets", "System.Runtime.InteropServices", "System.Security.Cryptography",
            "System.IO.Compression", "System.Net.NetworkInformation"
        ];
        await usingDirectiveTools.AddUsings(productServiceFile, unusedNamespaces, ct: TestContext.Current.CancellationToken);
        string afterAdd = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        List<string> actuallyAdded = unusedNamespaces
            .Where(ns => afterAdd.Contains($"using {ns};", StringComparison.Ordinal))
            .ToList();

        // Act — remove the unused usings we just added
        string removeResult = await TimingHelper.TimeAsync("Nop_RemoveUnusedUsings_GodClass_RoundTrip",
            () => usingDirectiveTools.RemoveUnusedUsings([productServiceFile]), output);
        string afterRemove = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);

        // Restore the shared fixture's file before asserting, so a failure can't leak into sibling tests
        await File.WriteAllBytesAsync(productServiceFile, originalBytes, TestContext.Current.CancellationToken);
        await fixture.WorkspaceManager.ReloadAsync(ct: TestContext.Current.CancellationToken);

        // Assert — the added usings were really added, then removed, restoring the canonical using
        // set (same directives, same sorted order, no duplicates).
        actuallyAdded.Count.ShouldBeGreaterThan(0, "At least some unused usings should have been added");
        output.WriteLine($"Added {actuallyAdded.Count} unused usings; remove result:\n{removeResult}");
        removeResult.ShouldContain("removed");

        foreach (string ns in actuallyAdded)
        {
            afterRemove.ShouldNotContain($"using {ns};");
        }

        UsingLines(afterRemove).ShouldBe(UsingLines(canonicalContent),
            "add-then-remove of only-unused usings should restore the canonical, sorted using set");
        return;

        static List<string> UsingLines(string content)
        {
            return SplitLines(content)
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("using ", StringComparison.Ordinal) && l.EndsWith(';'))
                .ToList();
        }
    }

    [Fact]
    public async Task InsertSymbol_After_RemoveViaReplaceContent_FileIdentical()
    {
        // Arrange
        CodeEditTools codeEdit = CreateEditTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        string originalContent = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        byte[] originalBytes = await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken);

        // Act — insert after GetProductByIdAsync
        await codeEdit.InsertSymbol(productServiceFile, "GetProductByIdAsync", "public virtual Task<int> RoundTripFoo() => Task.FromResult(42);", ct: TestContext.Current.CancellationToken);

        string afterInsert = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        afterInsert.ShouldContain("RoundTripFoo");

        // Find the exact inserted text by diffing
        string insertedText = ExtractInsertedText(originalContent, afterInsert);

        // Remove via replace_content using the exact inserted text
        await codeEdit.ReplaceContent([new ReplaceContentRequest(productServiceFile, insertedText, "")], ct: TestContext.Current.CancellationToken);

        // Assert
        byte[] afterBytes = await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken);
        afterBytes.ShouldBe(originalBytes,
            "ProductService.cs should be byte-identical after insert/remove round-trip");
    }

    [Fact]
    public async Task ReplaceContent_Regex_ReplaceBack_FileIdentical()
    {
        // Arrange
        CodeEditTools codeEdit = CreateEditTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        byte[] originalBytes = await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken);

        // Act — regex replace _productRepository → _prodRepo, then literal replace back
        await codeEdit.ReplaceContent([new ReplaceContentRequest(productServiceFile, @"_productRepository", "_prodRepoRegex", true)], ct: TestContext.Current.CancellationToken);
        await codeEdit.ReplaceContent([new ReplaceContentRequest(productServiceFile, "_prodRepoRegex", "_productRepository")], ct: TestContext.Current.CancellationToken);

        // Assert
        byte[] afterBytes = await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken);
        afterBytes.ShouldBe(originalBytes,
            "ProductService.cs should be byte-identical after regex replace round-trip");
    }

    [Fact]
    public async Task MultipleEdits_UndoAll_FileIdentical()
    {
        // Arrange
        CodeEditTools codeEdit = CreateEditTools(fixture);
        UsingDirectiveTools usingDirectiveTools = CreateUsingTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        byte[] originalBytes = await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken);

        // Act — chain of 3 edits, capturing state between each for precise undo

        // 1. Add a using
        string beforeUsing = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        await usingDirectiveTools.AddUsings(productServiceFile, ["System.Diagnostics"], ct: TestContext.Current.CancellationToken);
        string afterUsing = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);

        // 2. Insert a method (file state unchanged since afterUsing)
        string beforeInsert = afterUsing;
        await codeEdit.InsertSymbol(productServiceFile, "GetProductByIdAsync", "public virtual string UndoChainExtra() => \"extra\";", ct: TestContext.Current.CancellationToken);
        string afterInsert = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);

        // 3. Replace content in ProductService
        await codeEdit.ReplaceContent([new ReplaceContentRequest(productServiceFile, "_productRepository", "_prodRepoUndo")], ct: TestContext.Current.CancellationToken);

        // Verify all edits applied
        string midContent = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        midContent.ShouldContain("System.Diagnostics");
        midContent.ShouldContain("UndoChainExtra");
        midContent.ShouldContain("_prodRepoUndo");

        // Reverse — undo in reverse order
        // 3. Undo replace
        await codeEdit.ReplaceContent([new ReplaceContentRequest(productServiceFile, "_prodRepoUndo", "_productRepository")], ct: TestContext.Current.CancellationToken);

        // 2. Undo insert
        string insertedText = ExtractInsertedText(beforeInsert, afterInsert);
        await codeEdit.ReplaceContent([new ReplaceContentRequest(productServiceFile, insertedText, "")], ct: TestContext.Current.CancellationToken);

        // 1. Undo using
        string usingText = ExtractInsertedText(beforeUsing, afterUsing);
        await codeEdit.ReplaceContent([new ReplaceContentRequest(productServiceFile, usingText, "")], ct: TestContext.Current.CancellationToken);

        // Assert
        byte[] afterBytes = await File.ReadAllBytesAsync(productServiceFile, TestContext.Current.CancellationToken);
        afterBytes.ShouldBe(originalBytes,
            "ProductService.cs should be byte-identical after multi-edit undo");
    }

    private static string ExtractInsertedText(string before, string after)
    {
        string normalizedBefore = before.Replace("\r\n", "\n");
        string normalizedAfter = after.Replace("\r\n", "\n");
        int divergeIdx = FindDivergeIndex(normalizedBefore, normalizedAfter);
        int insertedLength = normalizedAfter.Length - normalizedBefore.Length;
        return normalizedAfter.Substring(divergeIdx, insertedLength);
    }

    private static int FindDivergeIndex(string a, string b)
    {
        int minLen = Math.Min(a.Length, b.Length);
        for (var i = 0; i < minLen; i++)
        {
            if (a[i] != b[i])
            {
                return i;
            }
        }

        return minLen;
    }
}
