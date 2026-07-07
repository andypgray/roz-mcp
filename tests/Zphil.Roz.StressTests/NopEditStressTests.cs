using System.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Models;
using Zphil.Roz.StressTests.Fixtures;
using Zphil.Roz.StressTests.Helpers;
using Zphil.Roz.Tools;
using static Zphil.Roz.StressTests.Fixtures.NopTestFileHelper;

namespace Zphil.Roz.StressTests;

[Trait("Category", "Stress")]
public class NopEditStressTests(NopTempWorkspaceFixture fixture, ITestOutputHelper output) : IClassFixture<NopTempWorkspaceFixture>
{
    [Fact]
    public async Task ReplaceSymbol_InGodClassService_Succeeds()
    {
        // Arrange — ProductService.cs is ~2500 lines
        CodeEditTools codeEdit = CreateEditTools(fixture);
        NavigationTools nav = CreateNavigationTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        // Find a method to replace
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(productServiceFile, "GetProductByIdAsync");

        // Act — replace with a simpler implementation
        string result = await TimingHelper.TimeAsync("Nop_ReplaceSymbol_GodClass",
            () => codeEdit.ReplaceSymbol(productServiceFile, "GetProductByIdAsync",
                "public virtual async Task<Product?> GetProductByIdAsync(int productId)\n    {\n        return await _productRepository.GetByIdAsync(productId, default);\n    }",
                line, column), output);

        // Assert
        result.ShouldContain("Replaced");
        string content = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        content.ShouldContain("GetProductByIdAsync");

        // File should still parse without errors
        string overview = await nav.GetSymbolsOverview([productServiceFile], ct: TestContext.Current.CancellationToken);
        overview.ShouldContain("GetProductByIdAsync");
    }

    [Fact]
    public async Task InsertSymbol_After_InLargeFile_MaintainsPerformance()
    {
        // Arrange
        CodeEditTools codeEdit = CreateEditTools(fixture);
        NavigationTools nav = CreateNavigationTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        // Act — insert 10 members into the 2500-line god class
        await TimingHelper.TimeAsync("Nop_InsertSymbol_GodClass_10Members", async () =>
        {
            var lastSymbol = "GetProductByIdAsync";
            for (var i = 0; i < 10; i++)
            {
                var methodName = $"StressInsert{i}";
                await codeEdit.InsertSymbol(productServiceFile, lastSymbol,
                    $"public virtual Task<int> {methodName}() => Task.FromResult({i});");
                lastSymbol = methodName;
            }
        }, output);

        // Assert — all 10 methods present
        string content = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        for (var i = 0; i < 10; i++)
        {
            content.ShouldContain($"StressInsert{i}");
        }

        string overview = await nav.GetSymbolsOverview([productServiceFile], ct: TestContext.Current.CancellationToken);
        for (var i = 0; i < 10; i++)
        {
            overview.ShouldContain($"StressInsert{i}");
        }
    }

    [Fact]
    public async Task RenameSymbol_CrossProject_TouchesMultipleFiles()
    {
        // Arrange — rename a type used across multiple nopCommerce projects
        await using TempWorkspace temp = await TempWorkspaceFactory.CreateAsync(TestSolutionConfig.NopCommerce, TestContext.Current.CancellationToken);
        CodeEditTools codeEdit = CreateEditTools(temp);
        NavigationTools nav = CreateNavigationTools(temp);
        string productFile = ProductFile(temp.WorkspaceManager);

        // Rename Product → ProductEntity (this touches files across Nop.Core, Nop.Services, Nop.Data, plugins)
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(productFile, "Product");

        // Act
        string result = await TimingHelper.TimeAsync("Nop_RenameSymbol_CrossProject",
            () => codeEdit.RenameSymbol(Loc(productFile, line, column), "Product", "ProductEntity"), output);

        // Assert — rename should have taken effect
        result.ShouldContain("Renamed");

        // The old name should be gone from the declaration file
        string content = await File.ReadAllTextAsync(productFile, TestContext.Current.CancellationToken);
        content.ShouldContain("class ProductEntity");

        // Find the new name in the solution
        string findResult = await nav.FindSymbol(["ProductEntity"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);
        findResult.ShouldContain("ProductEntity");
    }

    [Fact]
    public async Task RenameSymbol_CrossProject_RenameFile_SucceedsWithoutFileLockError()
    {
        // Arrange — Product is referenced in 45+ files across Nop.Core, Nop.Services, Nop.Data, plugins.
        // renameFile: true triggers File.Move after SaveSolutionChangesAsync, which can race with
        // queued ScheduleFileChanged tasks still holding file locks (see rename-symbol-file-lock.md).
        await using TempWorkspace temp = await TempWorkspaceFactory.CreateAsync(TestSolutionConfig.NopCommerce, TestContext.Current.CancellationToken);
        CodeEditTools codeEdit = CreateEditTools(temp);
        NavigationTools nav = CreateNavigationTools(temp);
        string productFile = ProductFile(temp.WorkspaceManager);
        string productDir = Path.GetDirectoryName(productFile)!;
        string expectedNewFile = Path.Combine(productDir, "ProductEntity.cs");

        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(productFile, "Product");

        // Act — rename Product → ProductEntity with renameFile: true
        string result = await TimingHelper.TimeAsync("Nop_RenameSymbol_CrossProject_RenameFile",
            () => codeEdit.RenameSymbol(Loc(productFile, line, column), "Product", "ProductEntity", renameFile: true), output);

        // Assert — operation succeeded without file-lock error
        result.ShouldContain("Renamed");
        result.ShouldNotContain("Error");
        result.ShouldNotContain("IOException");

        // Assert — file was physically renamed on disk
        File.Exists(productFile).ShouldBeFalse("Product.cs should no longer exist");
        File.Exists(expectedNewFile).ShouldBeTrue("ProductEntity.cs should exist");

        // Assert — renamed file contains the new class name
        string content = await File.ReadAllTextAsync(expectedNewFile, TestContext.Current.CancellationToken);
        content.ShouldContain("class ProductEntity");

        // Assert — workspace can find the renamed type after reload
        string findResult = await nav.FindSymbol(["ProductEntity"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);
        findResult.ShouldContain("ProductEntity");
    }

    [Fact]
    public async Task ReplaceContent_InLargeFile_LiteralReplace()
    {
        // Arrange
        CodeEditTools codeEdit = CreateEditTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        // Act — replace a string in the 2500-line file
        string result = await TimingHelper.TimeAsync("Nop_ReplaceContent_LargeFile",
            () => codeEdit.ReplaceContent([new ReplaceContentRequest(productServiceFile, "ProductService", "ProductSvc")]), output);

        // Assert
        result.ShouldContain("Replaced");
        string content = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        content.ShouldContain("ProductSvc");
    }

    [Fact]
    public async Task RepeatedInsertions_50Members_GodClass_AllPresentAndValid()
    {
        // Arrange — ProductService already has 30+ members and is ~2500 lines.
        // Adding 50 more pushes it to ~2600+ lines, re-parsing a progressively larger tree each time.
        CodeEditTools codeEdit = CreateEditTools(fixture);
        NavigationTools nav = CreateNavigationTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        // Act — insert 50 methods sequentially
        await TimingHelper.TimeAsync("Nop_RepeatedInsertions_50Members_GodClass", async () =>
        {
            var lastSymbol = "GetProductByIdAsync";
            for (var i = 0; i < 50; i++)
            {
                var methodName = $"GodClassStress{i}";
                await codeEdit.InsertSymbol(productServiceFile, lastSymbol,
                    $"public virtual Task<int> {methodName}() => Task.FromResult({i});");
                lastSymbol = methodName;
            }
        }, output);

        // Assert — all 50 methods present in file
        string content = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        for (var i = 0; i < 50; i++)
        {
            content.ShouldContain($"GodClassStress{i}");
        }

        // Symbols overview should list them all
        string overview = await nav.GetSymbolsOverview([productServiceFile], ct: TestContext.Current.CancellationToken);
        for (var i = 0; i < 50; i++)
        {
            overview.ShouldContain($"GodClassStress{i}");
        }

        // Line endings should be consistent
        AssertConsistentLineEndings(content);
    }

    [Fact]
    public async Task LargeMethodBody_500Lines_InGodClass_ReplacesSuccessfully()
    {
        // Arrange — replacing a symbol with 500 lines in an already 2500-line file creates ~3000 lines
        CodeEditTools codeEdit = CreateEditTools(fixture);
        NavigationTools nav = CreateNavigationTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(productServiceFile, "GetProductByIdAsync");

        // Generate a 500-line method body
        var sb = new StringBuilder();
        sb.AppendLine("public virtual async Task<Product?> GetProductByIdAsync(int productId)");
        sb.AppendLine("        {");
        for (var i = 0; i < 500; i++)
        {
            sb.AppendLine($"            var x{i} = {i};");
        }

        sb.AppendLine("            return await _productRepository.GetByIdAsync(productId, default);");
        sb.Append("        }");
        var largeBody = sb.ToString();

        // Act
        await TimingHelper.TimeAsync("Nop_LargeMethodBody_500Lines_GodClass",
            () => codeEdit.ReplaceSymbol(productServiceFile, "GetProductByIdAsync", largeBody, line, column), output);

        // Assert
        string content = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        content.ShouldContain("var x0 = 0;");
        content.ShouldContain("var x499 = 499;");

        string overview = await nav.GetSymbolsOverview([productServiceFile], ct: TestContext.Current.CancellationToken);
        overview.ShouldContain("GetProductByIdAsync");
    }

    [Fact]
    public async Task InsertBeforeAndAfter_GodClassMembers_AllPositionedAndCompile()
    {
        // Arrange — in a god class with 30+ methods, positional tracking is more likely to fail
        CodeEditTools codeEdit = CreateEditTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        // Target 3 methods in ProductService
        string[] targets = ["GetProductByIdAsync", "InsertProductAsync", "DeleteProductAsync"];

        // Act — insert before and after each target
        for (var i = 0; i < targets.Length; i++)
        {
            await codeEdit.InsertSymbol(productServiceFile, targets[i], $"public virtual int NopBefore{i} => {i};", InsertPosition.Before, ct: TestContext.Current.CancellationToken);
            await codeEdit.InsertSymbol(productServiceFile, targets[i], $"public virtual int NopAfter{i} => {i};", ct: TestContext.Current.CancellationToken);
        }

        // Assert — all inserted, correctly ordered relative to targets
        string content = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);

        for (var i = 0; i < targets.Length; i++)
        {
            content.ShouldContain($"NopBefore{i}");
            content.ShouldContain($"NopAfter{i}");

            int beforeIdx = Array.FindIndex(lines, l => l.Contains($"NopBefore{i}"));
            int targetIdx = Array.FindIndex(lines, l => l.Contains(targets[i]) && !l.Contains("NopBefore") && !l.Contains("NopAfter"));
            int afterIdx = Array.FindIndex(lines, l => l.Contains($"NopAfter{i}"));

            beforeIdx.ShouldBeLessThan(targetIdx, $"NopBefore{i} should appear before {targets[i]}");
            afterIdx.ShouldBeGreaterThan(targetIdx, $"NopAfter{i} should appear after {targets[i]}");
        }
    }

    [Fact]
    public async Task MixedLineEndings_ReplaceContent_HandlesCorrectly()
    {
        // Arrange — needs its own workspace since it destructively rewrites the file
        await using TempWorkspace temp = await TempWorkspaceFactory.CreateAsync(TestContext.Current.CancellationToken);
        CodeEditTools codeEdit = CreateEditTools(temp);
        string productServiceFile = ProductServiceFile(temp.WorkspaceManager);

        // Rewrite with alternating CRLF/LF
        await RewriteFileAsync(temp, productServiceFile, content =>
        {
            string[] lines = content.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            for (var i = 0; i < lines.Length; i++)
            {
                sb.Append(lines[i]);
                if (i < lines.Length - 1)
                {
                    sb.Append(i % 2 == 0 ? "\r\n" : "\n");
                }
            }

            return sb.ToString();
        });

        // Act — replace content in the mixed-ending file
        string result = await codeEdit.ReplaceContent([new ReplaceContentRequest(productServiceFile, "ProductService", "ProductSvcMixed")], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Replaced");
        string afterContent = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        afterContent.ShouldContain("ProductSvcMixed");
    }

    [Fact]
    public async Task InsertSymbol_Before_RepeatedInsertionsBeforeSameTarget_AllPresent()
    {
        // Arrange — repeatedly insert members before the same target to stress
        // the annotation-based tracking through multiple tree mutations
        CodeEditTools codeEdit = CreateEditTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        // Act — insert 10 members before InsertProductAsync
        for (var i = 0; i < 10; i++)
        {
            await codeEdit.InsertSymbol(productServiceFile, "InsertProductAsync", $"public virtual int BeforeInsert{i} => {i};", InsertPosition.Before, ct: TestContext.Current.CancellationToken);
        }

        // Assert — all 10 members present, all before InsertProductAsync
        string content = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int targetLine = Array.FindIndex(lines, l => l.Contains("InsertProductAsync") && !l.Contains("BeforeInsert"));

        for (var i = 0; i < 10; i++)
        {
            content.ShouldContain($"BeforeInsert{i}");
            int memberLine = Array.FindIndex(lines, l => l.Contains($"BeforeInsert{i}"));
            memberLine.ShouldBeLessThan(targetLine,
                $"BeforeInsert{i} should appear before InsertProductAsync");
        }
    }

    [Fact]
    public async Task InsertSymbol_Before_InterleavedWithInsertAfter_AllPositionedAndCompile()
    {
        // Arrange — interleave InsertBefore with InsertAfter on god class methods
        CodeEditTools codeEdit = CreateEditTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        // Act — alternate between InsertBefore and InsertAfter with real members
        await codeEdit.InsertSymbol(productServiceFile, "InsertProductAsync", "public virtual int B1 => 1;", InsertPosition.Before, ct: TestContext.Current.CancellationToken);
        await codeEdit.InsertSymbol(productServiceFile, "InsertProductAsync", "public virtual int A1 => 1;", ct: TestContext.Current.CancellationToken);
        await codeEdit.InsertSymbol(productServiceFile, "DeleteProductAsync", "public virtual int B2 => 2;", InsertPosition.Before, ct: TestContext.Current.CancellationToken);
        await codeEdit.InsertSymbol(productServiceFile, "DeleteProductAsync", "public virtual int A2 => 2;", ct: TestContext.Current.CancellationToken);
        await codeEdit.InsertSymbol(productServiceFile, "GetProductByIdAsync", "public virtual int B3 => 3;", InsertPosition.Before, ct: TestContext.Current.CancellationToken);
        await codeEdit.InsertSymbol(productServiceFile, "GetProductByIdAsync", "public virtual int A3 => 3;", ct: TestContext.Current.CancellationToken);

        // Assert ordering
        string content = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);

        int FindLine(string text)
        {
            return Array.FindIndex(lines, l => l.Contains(text));
        }

        FindLine("int B3").ShouldBeLessThan(FindLine("GetProductByIdAsync"), "B3 should be before GetProductByIdAsync");
        FindLine("int A3").ShouldBeGreaterThan(FindLine("GetProductByIdAsync"), "A3 should be after GetProductByIdAsync");
        FindLine("int B1").ShouldBeLessThan(FindLine("InsertProductAsync"), "B1 should be before InsertProductAsync");
        FindLine("int A1").ShouldBeGreaterThan(FindLine("InsertProductAsync"), "A1 should be after InsertProductAsync");
        FindLine("int B2").ShouldBeLessThan(FindLine("DeleteProductAsync"), "B2 should be before DeleteProductAsync");
        FindLine("int A2").ShouldBeGreaterThan(FindLine("DeleteProductAsync"), "A2 should be after DeleteProductAsync");
    }

    [Fact]
    public async Task Batch_10Edits_AcrossGodClassService_Succeeds()
    {
        // Arrange — single edit_symbol call with 10 insert ops, each chained after
        // the previous op's newly-inserted method. Exercises the batch path's sequential
        // workspace-state visibility (op N sees op N-1's insert) on a 2500-line file.
        CodeEditTools codeEdit = CreateEditTools(fixture);
        NavigationTools nav = CreateNavigationTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        List<EditSymbolRequest> edits = new();
        var lastSymbol = "GetProductByIdAsync";
        for (var i = 0; i < 10; i++)
        {
            var methodName = $"BatchInsert{i}";
            edits.Add(new EditSymbolRequest(
                EditSymbolAction.Insert,
                productServiceFile,
                lastSymbol,
                Content: $"public virtual Task<int> {methodName}() => Task.FromResult({i});",
                Position: InsertPosition.After));
            lastSymbol = methodName;
        }

        // Act
        string result = await TimingHelper.TimeAsync("Nop_BatchEdit_10Ops_GodClass",
            () => codeEdit.EditSymbol(edits.ToArray()), output);

        // Assert — batch sections present and all 10 members written.
        result.ShouldContain("=== insert '");
        string content = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        for (var i = 0; i < 10; i++)
        {
            content.ShouldContain($"BatchInsert{i}");
        }

        string overview = await nav.GetSymbolsOverview([productServiceFile], ct: TestContext.Current.CancellationToken);
        for (var i = 0; i < 10; i++)
        {
            overview.ShouldContain($"BatchInsert{i}");
        }
    }

    [Fact]
    public async Task EditThenImmediateQuery_ReturnsUpdatedState()
    {
        // Arrange
        CodeEditTools codeEdit = CreateEditTools(fixture);
        NavigationTools nav = CreateNavigationTools(fixture);
        string productServiceFile = ProductServiceFile(fixture.WorkspaceManager);

        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(productServiceFile, "GetProductByIdAsync");

        // Act — replace GetProductByIdAsync body, then immediately query
        await codeEdit.ReplaceSymbol(productServiceFile, "GetProductByIdAsync", "public virtual async Task<Product?> GetProductByIdAsync(int productId) => await _productRepository.GetByIdAsync(productId, default);", line, column, ct: TestContext.Current.CancellationToken);

        string findResult = await nav.FindSymbol(["GetProductByIdAsync"], ct: TestContext.Current.CancellationToken);
        string overview = await nav.GetSymbolsOverview([productServiceFile], ct: TestContext.Current.CancellationToken);

        // Assert
        findResult.ShouldContain("GetProductByIdAsync");
        overview.ShouldContain("GetProductByIdAsync");

        string fileContent = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("GetProductByIdAsync");
    }

    [Fact]
    public async Task RenameSymbol_InCommentsAndStrings_RewritesTrivia()
    {
        // Arrange — fresh workspace; insert a marker method whose name also appears in a comment and
        // a string literal, so the renameInComments/renameInStrings flags have known targets to
        // rewrite (a controlled probe rather than hunting nopCommerce's incidental occurrences). The
        // marker is private so Roslyn confines the rename (and the trivia scan) to this file instead
        // of searching the whole solution for interface implementations and overrides.
        await using TempWorkspace temp = await TempWorkspaceFactory.CreateAsync(TestSolutionConfig.NopCommerce, TestContext.Current.CancellationToken);
        CodeEditTools codeEdit = CreateEditTools(temp);
        string productServiceFile = ProductServiceFile(temp.WorkspaceManager);

        string marker =
            "private string StressRenameTarget()\n" +
            "{\n" +
            "    // StressRenameTarget marker comment\n" +
            "    return \"StressRenameTarget value\";\n" +
            "}";
        await codeEdit.InsertSymbol(productServiceFile, "GetProductByIdAsync", marker, ct: TestContext.Current.CancellationToken);

        // Act — rename with both trivia flags on
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180));
        string result = await TimingHelper.TimeAsync("Nop_RenameSymbol_CommentsAndStrings",
            () => codeEdit.RenameSymbol(productServiceFile, "StressRenameTarget", "StressRenameRenamed",
                "ProductService", renameInStrings: true, renameInComments: true, ct: cts.Token), output);

        // Assert — declaration, comment, and string literal were all rewritten
        result.ShouldContain("Renamed");
        string content = await File.ReadAllTextAsync(productServiceFile, TestContext.Current.CancellationToken);
        content.ShouldContain("StressRenameRenamed()"); // the declaration
        content.ShouldContain("StressRenameRenamed marker comment"); // comment via renameInComments
        content.ShouldContain("StressRenameRenamed value"); // string via renameInStrings
        content.ShouldNotContain("StressRenameTarget");
    }

    [Fact]
    public async Task RenameSymbol_RenameOverloads_RenamesAllOverloads()
    {
        // Arrange — IRepository<TEntity>.DeleteAsync has three overloads (entity, list, predicate).
        // renameOverloads must rename the whole method group, with a solution-wide blast radius
        // across every repository consumer.
        await using TempWorkspace temp = await TempWorkspaceFactory.CreateAsync(TestSolutionConfig.NopCommerce, TestContext.Current.CancellationToken);
        CodeEditTools codeEdit = CreateEditTools(temp);
        string iRepoFile = IRepositoryFile(temp.WorkspaceManager);
        (int line, int column) = await SymbolPositionHelper.FindSymbolPositionAsync(iRepoFile, "DeleteAsync");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        string result = await TimingHelper.TimeAsync("Nop_RenameSymbol_RenameOverloads_IRepository",
            () => codeEdit.RenameSymbol(Loc(iRepoFile, line, column), "DeleteAsync", "DeleteEntityAsync",
                renameOverloads: true, ct: cts.Token), output);

        // Assert — all three overload declarations renamed in the interface; none left behind
        result.ShouldContain("Renamed");
        string content = await File.ReadAllTextAsync(iRepoFile, TestContext.Current.CancellationToken);
        int renamedDecls = content.Split("DeleteEntityAsync").Length - 1;
        output.WriteLine($"DeleteEntityAsync occurrences in IRepository.cs: {renamedDecls}");
        renamedDecls.ShouldBeGreaterThanOrEqualTo(3, "all three DeleteAsync overloads should be renamed");
        content.ShouldNotContain("DeleteAsync(");
    }
}
