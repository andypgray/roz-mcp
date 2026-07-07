using System.Text;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.UsingDirectiveToolTests;

public class AddUsingsTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task AddUsings_NewUsings_AddsInSortedOrder()
    {
        // Arrange
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — Circle.cs has no usings; add two
        string result = await directiveTools.AddUsings(circleFile, ["Microsoft.CodeAnalysis", "System.Numerics"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Added 2 using(s)");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int systemIdx = Array.FindIndex(lines, l => l.Contains("using System.Numerics;"));
        int msIdx = Array.FindIndex(lines, l => l.Contains("using Microsoft.CodeAnalysis;"));
        systemIdx.ShouldBeGreaterThan(-1);
        msIdx.ShouldBeGreaterThan(-1);
        // System.* should come before Microsoft.*
        systemIdx.ShouldBeLessThan(msIdx);
    }

    [Fact]
    public async Task AddUsings_DuplicateUsing_SkipsExisting()
    {
        // Arrange — ShapeService.cs has "using TestFixture.Shapes;"
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        // Act
        string result = await directiveTools.AddUsings(serviceFile, ["TestFixture.Shapes"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Already present");
        result.ShouldContain("TestFixture.Shapes");
        result.ShouldContain("No new usings added");
    }

    [Fact]
    public async Task AddUsings_MixedNewAndExisting_AddsOnlyNew()
    {
        // Arrange — ShapeService.cs has "using TestFixture.Shapes;"
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        // Act
        string result = await directiveTools.AddUsings(serviceFile, ["TestFixture.Shapes", "System.Numerics"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Added 1 using(s)");
        result.ShouldContain("Already present: TestFixture.Shapes");
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldContain("using System.Numerics;");
    }

    [Fact]
    public async Task AddUsings_EmptyArray_ThrowsUserError()
    {
        // Arrange
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);

        // Act & Assert
        await Should.ThrowAsync<UserErrorException>(() => directiveTools.AddUsings(CircleFile(Fixture), []));
    }

    [Fact]
    public async Task AddUsings_SystemNamespaces_SortedBeforeOthers()
    {
        // Arrange
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — add in reverse order; System.* should still come first
        string result = await directiveTools.AddUsings(circleFile, ["Xunit", "System.Numerics", "System.Diagnostics", "Microsoft.Extensions.Logging"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Added 4 using(s)");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int diagIdx = Array.FindIndex(lines, l => l.TrimStart().StartsWith("using System.Diagnostics;"));
        int numIdx = Array.FindIndex(lines, l => l.TrimStart().StartsWith("using System.Numerics;"));
        int msIdx = Array.FindIndex(lines, l => l.TrimStart().StartsWith("using Microsoft.Extensions.Logging;"));
        int xunitIdx = Array.FindIndex(lines, l => l.TrimStart().StartsWith("using Xunit;"));

        diagIdx.ShouldBeLessThan(numIdx);
        numIdx.ShouldBeLessThan(msIdx);
        msIdx.ShouldBeLessThan(xunitIdx);
    }

    [Fact]
    public async Task AddUsings_PreservesEncoding_BomFile()
    {
        // Arrange — write Circle.cs with a UTF-8 BOM
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(circleFile, content, new UTF8Encoding(true), TestContext.Current.CancellationToken);

        // Act
        await directiveTools.AddUsings(circleFile, ["System.Numerics"], ct: TestContext.Current.CancellationToken);

        // Assert — BOM should be preserved
        await circleFile.ShouldHaveBomAsync();
    }

    [Fact]
    public async Task AddUsings_PreservesLineEndings_CrlfFile()
    {
        // Arrange
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await EnsureCrlfAsync(Fixture, circleFile);

        // Act
        await directiveTools.AddUsings(circleFile, ["System.Numerics"], ct: TestContext.Current.CancellationToken);

        // Assert — CRLF should be preserved
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldHaveNoBareLineFeed();
    }

    [Fact]
    public async Task AddUsings_SortDisabled_AppendsWithoutReordering()
    {
        // Arrange — ShapeService.cs has "using TestFixture.Shapes;" already
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        // Act — add System.Numerics with sorting disabled; it should appear after existing using
        string result = await directiveTools.AddUsings(serviceFile, ["System.Numerics"], false, TestContext.Current.CancellationToken);

        // Assert — System.Numerics added but NOT sorted before TestFixture.Shapes
        result.ShouldContain("Added 1 using(s)");
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        int existingIdx = content.IndexOf("using TestFixture.Shapes;", StringComparison.Ordinal);
        int newIdx = content.IndexOf("using System.Numerics;", StringComparison.Ordinal);
        existingIdx.ShouldBeGreaterThan(-1);
        newIdx.ShouldBeGreaterThan(-1);
        // Without sorting, existing should stay before the newly appended one
        existingIdx.ShouldBeLessThan(newIdx);
    }

    [Fact]
    public async Task AddUsings_NonExistentFile_ThrowsUserError()
    {
        // Arrange
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string bogusFile = Path.Combine(Fixture.WorkspaceManager.SolutionDirectory!, "DoesNotExist.cs");

        // Act & Assert
        await Should.ThrowAsync<UserErrorException>(() => directiveTools.AddUsings(bogusFile, ["System.Linq"]));
    }

    [Fact]
    public async Task AddUsings_FileScopedNamespace_AddsAboveNamespace()
    {
        // Arrange — Circle.cs uses file-scoped namespace
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act
        await directiveTools.AddUsings(circleFile, ["System.Numerics"], ct: TestContext.Current.CancellationToken);

        // Assert — using should appear before the namespace declaration
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        int usingIdx = content.IndexOf("using System.Numerics;", StringComparison.Ordinal);
        int namespaceIdx = content.IndexOf("namespace TestFixture.Shapes;", StringComparison.Ordinal);
        usingIdx.ShouldBeGreaterThan(-1);
        namespaceIdx.ShouldBeGreaterThan(-1);
        usingIdx.ShouldBeLessThan(namespaceIdx);
    }

    [Fact]
    public async Task AddUsings_ImplicitUsing_SkipsAndReportsGloballyImported()
    {
        // Arrange — System.Linq is an implicit using in the test fixture (ImplicitUsings=enable)
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act
        string result = await directiveTools.AddUsings(circleFile, ["System.Linq"], ct: TestContext.Current.CancellationToken);

        // Assert — should not be added, reported as globally imported
        result.ShouldContain("No new usings added");
        result.ShouldContain("Already available via global using");
        result.ShouldContain("System.Linq");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("using System.Linq;");
    }

    [Fact]
    public async Task AddUsings_MixOfImplicitAndNew_AddsOnlyNonImplicit()
    {
        // Arrange
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — System.Linq is implicit, System.Numerics is not
        string result = await directiveTools.AddUsings(circleFile, ["System.Linq", "System.Numerics"], ct: TestContext.Current.CancellationToken);

        // Assert — only System.Numerics should be added
        result.ShouldContain("Added 1 using(s)");
        result.ShouldContain("System.Numerics");
        result.ShouldContain("Already available via global using");
        result.ShouldContain("System.Linq");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("using System.Numerics;");
        content.ShouldNotContain("using System.Linq;");
    }

    [Fact]
    public async Task AddUsings_ExplicitGlobalUsing_SkipsAndReportsGloballyImported()
    {
        // Arrange — GlobalUsings.cs has "global using TestFixture.Services;"
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act — TestFixture.Services is available via explicit global using in GlobalUsings.cs
        string result = await directiveTools.AddUsings(circleFile, ["TestFixture.Services"], ct: TestContext.Current.CancellationToken);

        // Assert — should be detected as globally imported
        result.ShouldContain("No new usings added");
        result.ShouldContain("Already available via global using");
        result.ShouldContain("TestFixture.Services");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("using TestFixture.Services;");
    }

    [Theory]
    [InlineData("this.is.not.a.valid namespace")]
    [InlineData("System Linq")]
    [InlineData("foo@bar")]
    public async Task AddUsings_InvalidNamespace_ThrowsUserError(string invalidUsing)
    {
        // Arrange
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);

        // Act & Assert
        await Should.ThrowAsync<UserErrorException>(() => directiveTools.AddUsings(CircleFile(Fixture), [invalidUsing]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public async Task AddUsings_EmptyOrWhitespace_ThrowsUserError(string emptyUsing)
    {
        // Arrange
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);

        // Act & Assert
        await Should.ThrowAsync<UserErrorException>(() => directiveTools.AddUsings(CircleFile(Fixture), [emptyUsing]));
    }

    [Fact]
    public async Task AddUsings_ValidAlias_AddsCorrectly()
    {
        // Arrange
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act
        string result = await directiveTools.AddUsings(circleFile, ["Json = System.Text.Json"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Added 1 using(s)");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("using Json = System.Text.Json;");
    }

    [Fact]
    public async Task AddUsings_FileNotInSolution_FallsBackToSyntaxOnly()
    {
        // Arrange — create a .cs file outside the solution directory
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.cs");
        try
        {
            await File.WriteAllTextAsync(tempFile, "namespace Standalone;\r\n\r\npublic class Foo { }\r\n", TestContext.Current.CancellationToken);

            // Act — file is not in the solution, so implicit using check is skipped
            string result = await directiveTools.AddUsings(tempFile, ["System.Linq"], ct: TestContext.Current.CancellationToken);

            // Assert — added normally because global using check was skipped (no document in solution)
            result.ShouldContain("Added 1 using(s)");
            string content = await File.ReadAllTextAsync(tempFile, TestContext.Current.CancellationToken);
            content.ShouldContain("using System.Linq;");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task AddUsings_NonCsFile_Throws()
    {
        // Arrange — create a .csproj file (non-C# source)
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string tempFile = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.csproj");
        try
        {
            await File.WriteAllTextAsync(tempFile, "<Project Sdk=\"Microsoft.NET.Sdk\">\r\n</Project>\r\n", TestContext.Current.CancellationToken);

            // Act & Assert — should reject non-.cs files
            UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() => directiveTools.AddUsings(tempFile, ["System.Linq"]));
            ex.Message.ShouldContain(".cs");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ── Preprocessor directive protection ────────────────────────────────

    [Fact]
    public async Task AddUsings_ConditionalBlock_InsertsOutsideBlock()
    {
        // Arrange — file with a #if/#else/#endif conditional using block.
        // System.Text sorts after System.Numerics (inside #if) but before TestFixture.Shapes
        // (after #endif), so the naive sorted insertion would place it at depth 1 — inside the block.
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        await RewriteFileAsync(Fixture, serviceFile, _ =>
            "using System.Collections;\r\n"
            + "#if NET8_0_OR_GREATER\r\n"
            + "using System.Numerics;\r\n"
            + "#else\r\n"
            + "using System.Threading;\r\n"
            + "#endif\r\n"
            + "using TestFixture.Shapes;\r\n"
            + "\r\n"
            + "namespace TestFixture.Services;\r\n\r\n"
            + "public class ShapeService\r\n{\r\n"
            + "    public IShape? Shape { get; set; }\r\n}\r\n");

        // Act — System.Text sorts after System.Numerics but before TestFixture.Shapes
        string result = await directiveTools.AddUsings(serviceFile, ["System.Text"], ct: TestContext.Current.CancellationToken);

        // Assert — System.Text must be placed OUTSIDE the conditional block, after #endif
        result.ShouldContain("Added 1 using(s)");
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldContain("using System.Text;");
        content.ShouldContain("#if NET8_0_OR_GREATER");
        content.ShouldContain("#endif");

        int endifIdx = content.IndexOf("#endif", StringComparison.Ordinal);
        int textIdx = content.IndexOf("using System.Text;", StringComparison.Ordinal);
        textIdx.ShouldBeGreaterThan(endifIdx, "System.Text must be after #endif, not inside the conditional block");
    }

    [Fact]
    public async Task AddUsings_UsingSortsBeforeConditionalBlock_InsertsBeforeBlock()
    {
        // Arrange — adding a using that sorts before the conditional block should stay before it
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        await RewriteFileAsync(Fixture, serviceFile, _ =>
            "#if NET8_0_OR_GREATER\r\n"
            + "using System.Numerics;\r\n"
            + "#else\r\n"
            + "using System.Threading;\r\n"
            + "#endif\r\n"
            + "using TestFixture.Shapes;\r\n"
            + "\r\n"
            + "namespace TestFixture.Services;\r\n\r\n"
            + "public class ShapeService\r\n{\r\n"
            + "    public IShape? Shape { get; set; }\r\n}\r\n");

        // Act — System.Diagnostics sorts before System.Numerics; insertion at index 0 is depth 0
        string result = await directiveTools.AddUsings(serviceFile, ["System.Diagnostics"], ct: TestContext.Current.CancellationToken);

        // Assert — System.Diagnostics is placed before the #if block
        result.ShouldContain("Added 1 using(s)");
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        int diagIdx = content.IndexOf("using System.Diagnostics;", StringComparison.Ordinal);
        int ifIdx = content.IndexOf("#if NET8_0_OR_GREATER", StringComparison.Ordinal);
        diagIdx.ShouldBeLessThan(ifIdx, "System.Diagnostics should be before the #if block");
    }

    // ── Dedup & trivia preservation (F7/F8) ──────────────────────────────

    [Fact]
    public async Task AddUsings_ReAddingExistingAlias_IsDeduplicated()
    {
        // Arrange — UsingMixExample.cs already has "using SL = System.Collections.Generic.List<int>;"
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string mixFile = UsingMixExampleFile(Fixture);

        // Act — re-add the identical alias
        string result = await directiveTools.AddUsings(mixFile, ["SL = System.Collections.Generic.List<int>"], ct: TestContext.Current.CancellationToken);

        // Assert — recognised as already present, no duplicate alias line (would be CS1537)
        result.ShouldContain("No new usings added");
        result.ShouldContain("Already present");
        string content = await File.ReadAllTextAsync(mixFile, TestContext.Current.CancellationToken);
        (content.Split("using SL =").Length - 1).ShouldBe(1, "the existing alias must not be duplicated");
    }

    [Fact]
    public async Task AddUsings_StaticUsing_BuildsWellFormedDirective()
    {
        // Arrange — Circle.cs has no usings
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);

        // Act
        string result = await directiveTools.AddUsings(circleFile, ["static System.Math"], ct: TestContext.Current.CancellationToken);

        // Assert — a well-formed "using static System.Math;", not a mangled directive
        result.ShouldContain("Added 1 using(s)");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("using static System.Math;");
    }

    [Fact]
    public async Task AddUsings_ReAddingExistingStatic_IsDeduplicated()
    {
        // Arrange — UsingMixExample.cs already has "using static System.Math;"
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string mixFile = UsingMixExampleFile(Fixture);

        // Act — re-add the identical static using
        string result = await directiveTools.AddUsings(mixFile, ["static System.Math"], ct: TestContext.Current.CancellationToken);

        // Assert — recognised as already present, no duplicate static line
        result.ShouldContain("No new usings added");
        string content = await File.ReadAllTextAsync(mixFile, TestContext.Current.CancellationToken);
        (content.Split("using static System.Math;").Length - 1).ShouldBe(1, "the existing static using must not be duplicated");
    }

    [Fact]
    public async Task AddUsings_InsertAtTop_KeepsFileHeaderAboveNewUsing()
    {
        // Arrange — Circle.cs: copyright header + a single using that the new one sorts before
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, c => CopyrightHeader + "using System.Text;\r\n" + c);

        // Act — System.Collections sorts before System.Text, so it inserts at index 0
        string result = await directiveTools.AddUsings(circleFile, ["System.Collections"], ct: TestContext.Current.CancellationToken);

        // Assert — the file header must stay above the inserted top using, not get sandwiched
        result.ShouldContain("Added 1 using(s)");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int headerIdx = Array.FindIndex(lines, l => l.Contains("Copyright (c) Test Corp"));
        int firstUsingIdx = Array.FindIndex(lines, l => l.TrimStart().StartsWith("using "));
        headerIdx.ShouldBeGreaterThan(-1);
        firstUsingIdx.ShouldBeGreaterThan(-1);
        headerIdx.ShouldBeLessThan(firstUsingIdx, "the file header must stay above the inserted top using");
    }

    [Fact]
    public async Task AddUsings_InsertAfterNeighborWithTrailingComment_DoesNotDuplicateComment()
    {
        // Arrange — Circle.cs: a first using with a trailing "// note" comment, then the body
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, c => "using System;  // note\r\n" + c);

        // Act — System.Collections sorts after System, so it inserts at index 1, after the neighbor
        string result = await directiveTools.AddUsings(circleFile, ["System.Collections"], ct: TestContext.Current.CancellationToken);

        // Assert — the neighbor's "// note" is not copied onto the new using
        result.ShouldContain("Added 1 using(s)");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        (content.Split("// note").Length - 1).ShouldBe(1, "the neighbor's trailing comment must not be duplicated");
        string[] lines = SplitLines(content);
        int newUsingIdx = Array.FindIndex(lines, l => l.Contains("using System.Collections;"));
        newUsingIdx.ShouldBeGreaterThan(-1);
        lines[newUsingIdx].ShouldNotContain("// note");
    }
}
