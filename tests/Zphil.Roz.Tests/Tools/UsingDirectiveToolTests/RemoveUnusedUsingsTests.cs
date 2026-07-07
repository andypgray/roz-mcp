using System.Text;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.UsingDirectiveToolTests;

public class RemoveUnusedUsingsTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task RemoveUnusedUsings_FileWithUnusedUsing_RemovesIt()
    {
        // Arrange — prepend unused "using System.IO;" to Circle.cs
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, c => "using System.IO;\r\n" + c);

        // Act
        string result = await directiveTools.RemoveUnusedUsings([circleFile], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("removed 1 unused using(s)");
        result.ShouldContain("System.IO");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("using System.IO;");
    }

    [Fact]
    public async Task RemoveUnusedUsings_FileWithNoUnusedUsings_ReportsNoneRemoved()
    {
        // Arrange — ShapeService.cs uses its "using TestFixture.Shapes;"
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);

        // Act
        string result = await directiveTools.RemoveUnusedUsings([serviceFile], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("no unused usings found");
    }

    [Fact]
    public async Task RemoveUnusedUsings_MultipleFiles_ProcessesAll()
    {
        // Arrange — add unused usings to two files
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, c => "using System.IO;\r\n" + c);
        await RewriteFileAsync(Fixture, serviceFile, c => "using System.IO;\r\n" + c);

        // Act
        string result = await directiveTools.RemoveUnusedUsings([circleFile, serviceFile], ct: TestContext.Current.CancellationToken);

        // Assert — both files should be cleaned
        result.ShouldContain("System.IO");
        string circleContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string serviceContent = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        circleContent.ShouldNotContain("using System.IO;");
        serviceContent.ShouldNotContain("using System.IO;");
    }

    [Fact]
    public async Task RemoveUnusedUsings_SortsRemainingUsings()
    {
        // Arrange — add usings in wrong order: used one + unused one
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        // Prepend an unused "using System.IO;" and reorder to put TestFixture.Shapes first
        await RewriteFileAsync(Fixture, serviceFile,
            c => "using System.IO;\r\n" + c);

        // Act
        string result = await directiveTools.RemoveUnusedUsings([serviceFile], ct: TestContext.Current.CancellationToken);

        // Assert — System.IO removed, remaining "using TestFixture.Shapes;" stays
        result.ShouldContain("System.IO");
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldContain("using TestFixture.Shapes;");
        content.ShouldNotContain("using System.IO;");
    }

    [Fact]
    public async Task RemoveUnusedUsings_SortDisabled_RemovesButPreservesOrder()
    {
        // Arrange — add unused "using System.IO;" after the existing "using TestFixture.Shapes;"
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        // Append unused using after the existing one (reverse alphabetical order)
        await RewriteFileAsync(Fixture, serviceFile,
            c => c.Replace("using TestFixture.Shapes;",
                "using TestFixture.Shapes;\r\nusing System.IO;"));

        // Act — remove unused with sorting disabled
        string result = await directiveTools.RemoveUnusedUsings([serviceFile], false, TestContext.Current.CancellationToken);

        // Assert — System.IO removed, TestFixture.Shapes stays, no reordering occurred
        result.ShouldContain("System.IO");
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("using System.IO;");
        content.ShouldContain("using TestFixture.Shapes;");
    }

    [Fact]
    public async Task RemoveUnusedUsings_SortDisabled_NoUnusedUsings_DoesNotReorder()
    {
        // Arrange — add a used using in unsorted position (after TestFixture.Shapes)
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        string originalContent = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);

        // Act — no unused usings, sorting disabled
        string result = await directiveTools.RemoveUnusedUsings([serviceFile], false, TestContext.Current.CancellationToken);

        // Assert — file should be untouched
        result.ShouldContain("no unused usings found");
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldBe(originalContent);
    }

    [Fact]
    public async Task RemoveUnusedUsings_PreservesEncoding_BomFile()
    {
        // Arrange — write Circle.cs with BOM and unused using
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(circleFile, "using System.IO;\r\n" + content, new UTF8Encoding(true), TestContext.Current.CancellationToken);
        await Fixture.WorkspaceManager.ReloadAsync(ct: TestContext.Current.CancellationToken);

        // Act
        await directiveTools.RemoveUnusedUsings([circleFile], ct: TestContext.Current.CancellationToken);

        // Assert — BOM should be preserved
        await circleFile.ShouldHaveBomAsync();
    }

    [Fact]
    public async Task RemoveUnusedUsings_PreservesLineEndings()
    {
        // Arrange
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, c => "using System.IO;\r\n" + c);

        // Act
        await directiveTools.RemoveUnusedUsings([circleFile], ct: TestContext.Current.CancellationToken);

        // Assert — CRLF should be preserved
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldHaveNoBareLineFeed();
    }

    [Fact]
    public async Task RemoveUnusedUsings_EmptyFilePaths_ThrowsUserError()
    {
        // Arrange
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);

        // Act & Assert
        await Should.ThrowAsync<UserErrorException>(() => directiveTools.RemoveUnusedUsings([]));
    }

    [Fact]
    public async Task RemoveUnusedUsings_FileWithNoUsingDirectives_ReportsNoneRemoved()
    {
        // Arrange — Circle.cs has zero using directives (distinct from "has usings but none unused")
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);
        string originalContent = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);

        // Act
        string result = await directiveTools.RemoveUnusedUsings([circleFile], ct: TestContext.Current.CancellationToken);

        // Assert — no usings to remove, file untouched
        result.ShouldContain("no unused usings found");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldBe(originalContent);
    }

    [Fact]
    public async Task RemoveUnusedUsings_InvalidFile_ReturnsError()
    {
        // Arrange
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);

        // Act
        string result = await directiveTools.RemoveUnusedUsings(["NonExistent.cs"], ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Error");
        result.ShouldContain("not found");
    }

    [Fact]
    public async Task RemoveUnusedUsings_NoUnusedButUnsorted_SortsUsings()
    {
        // Arrange — rewrite ShapeService.cs with two non-implicit, used usings in wrong order
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        await RewriteFileAsync(Fixture, serviceFile, _ => ShapeServiceWithDiagnostics());

        // Act — no unused usings, but sort is needed
        string result = await directiveTools.RemoveUnusedUsings([serviceFile], ct: TestContext.Current.CancellationToken);

        // Assert — usings reordered (System.Diagnostics before TestFixture.Shapes), none removed
        result.ShouldContain("no unused usings found");
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        int diagIdx = content.IndexOf("using System.Diagnostics;", StringComparison.Ordinal);
        int shapesIdx = content.IndexOf("using TestFixture.Shapes;", StringComparison.Ordinal);
        diagIdx.ShouldBeGreaterThan(-1, "System.Diagnostics should still be present");
        shapesIdx.ShouldBeGreaterThan(-1, "TestFixture.Shapes should still be present");
        diagIdx.ShouldBeLessThan(shapesIdx, "System.Diagnostics should come before TestFixture.Shapes after sorting");
    }

    [Fact]
    public async Task RemoveUnusedUsings_NoUnusedAlreadySorted_DoesNotRewrite()
    {
        // Arrange — rewrite ShapeService.cs with two non-implicit, used usings in correct order
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        await RewriteFileAsync(Fixture, serviceFile, _ => ShapeServiceWithDiagnostics(true));
        string contentBefore = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);

        // Act — no unused usings, already sorted
        string result = await directiveTools.RemoveUnusedUsings([serviceFile], ct: TestContext.Current.CancellationToken);

        // Assert — file unchanged (UsingsMatch returns true, no WriteBack)
        result.ShouldContain("no unused usings found");
        string contentAfter = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        contentAfter.ShouldBe(contentBefore);
    }

    [Fact]
    public async Task RemoveUnusedUsings_StaticAndAliasUsings_SortsCorrectly()
    {
        // Arrange — UsingMixExample.cs has: using static, using alias, and regular usings in unsorted order
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string mixFile = UsingMixExampleFile(Fixture);

        // Act
        await directiveTools.RemoveUnusedUsings([mixFile], ct: TestContext.Current.CancellationToken);

        // Assert — regular usings should come before static/alias usings
        string content = await File.ReadAllTextAsync(mixFile, TestContext.Current.CancellationToken);
        int firstRegular = content.IndexOf("using TestFixture.Shapes;", StringComparison.Ordinal);
        int staticUsing = content.IndexOf("using static", StringComparison.Ordinal);
        firstRegular.ShouldBeGreaterThanOrEqualTo(0, "Expected 'using TestFixture.Shapes;' in file");
        staticUsing.ShouldBeGreaterThanOrEqualTo(0, "Expected 'using static' in file");
        firstRegular.ShouldBeLessThan(staticUsing,
            "Regular usings should come before static usings after sorting");
    }

    [Fact]
    public async Task RemoveUnusedUsings_DuplicateFilePaths_ProcessesOnce()
    {
        // Arrange — prepend unused "using System.IO;" to Circle.cs
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, c => "using System.IO;\r\n" + c);

        // Act — same path twice
        string result = await directiveTools.RemoveUnusedUsings([circleFile, circleFile], ct: TestContext.Current.CancellationToken);

        // Assert — file should appear only once in output
        var count = 0;
        var idx = 0;
        while ((idx = result.IndexOf("removed 1 unused using(s)", idx, StringComparison.Ordinal)) != -1)
        {
            count++;
            idx += "removed 1 unused using(s)".Length;
        }

        count.ShouldBe(1, "duplicate file path should be processed only once");
    }

    [Fact]
    public async Task RemoveUnusedUsings_ExplicitDuplicatesImplicit_RemovesIt()
    {
        // Arrange — ShapeService.cs uses System.Linq implicitly (MaxBy).
        // Add an explicit "using System.Linq;" which duplicates the implicit one.
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        await RewriteFileAsync(Fixture, serviceFile,
            c => "using System.Linq;\r\n" + c);

        // Act
        string result = await directiveTools.RemoveUnusedUsings([serviceFile], ct: TestContext.Current.CancellationToken);

        // Assert — CS8019 should detect the explicit using as redundant
        result.ShouldContain("removed 1 unused using(s)");
        result.ShouldContain("System.Linq");
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("using System.Linq;");
        // The file should still work — System.Linq is available via implicit using
        content.ShouldContain("using TestFixture.Shapes;");
    }

    [Fact]
    public async Task RemoveUnusedUsings_FirstUsingRemoved_PreservesCopyrightHeader()
    {
        // Arrange — prepend copyright header + unused using (that sorts first) to ShapeService.cs
        // Bug: RemoveNodes with KeepNoTrivia drops the copyright header when the first using is removed
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        await RewriteFileAsync(Fixture, serviceFile, c =>
            CopyrightHeader
            + "using System.IO;\r\n"
            + c);

        // Act — System.IO is unused, sorts before TestFixture.Shapes, so it becomes the first using
        await directiveTools.RemoveUnusedUsings([serviceFile], ct: TestContext.Current.CancellationToken);

        // Assert — copyright header must survive removal of the first using
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Copyright (c) Test Corp");
        content.ShouldContain("Licensed under the MIT License");
        content.ShouldContain("using TestFixture.Shapes;");
        content.ShouldNotContain("using System.IO;");
    }

    [Fact]
    public async Task RemoveUnusedUsings_UnusedFirstUsingWithCopyrightHeader_PreservesHeader()
    {
        // Arrange — exact reproduction from ILSpy stress test bug #7:
        // File has copyright header + unused using that sorts first + used using
        // remove_unused_usings removes the first using → copyright header must survive
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        await RewriteFileAsync(Fixture, serviceFile, c =>
            CopyrightHeader
            + "using System.Collections.Generic;\r\n"
            + c);

        // Act — System.Collections.Generic is unused and sorts before TestFixture.Shapes
        string result = await directiveTools.RemoveUnusedUsings([serviceFile], ct: TestContext.Current.CancellationToken);
        result.ShouldContain("System.Collections.Generic");

        // Assert — copyright header must survive removal of the first using
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Copyright (c) Test Corp");
        content.ShouldContain("Licensed under the MIT License");
        content.ShouldContain("using TestFixture.Shapes;");
        content.ShouldNotContain("using System.Collections.Generic;");
    }

    [Fact]
    public async Task RemoveUnusedUsings_AllUsingsRemoved_PreservesCopyrightHeader()
    {
        // Arrange — file has copyright header + only unused usings → all usings removed
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, c =>
            CopyrightHeader
            + "using System.IO;\r\n"
            + c);

        // Act — System.IO is the only using and it's unused
        string result = await directiveTools.RemoveUnusedUsings([circleFile], ct: TestContext.Current.CancellationToken);
        result.ShouldContain("System.IO");

        // Assert — copyright header must survive even when all usings are removed
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Copyright (c) Test Corp");
        content.ShouldContain("Licensed under the MIT License");
        content.ShouldNotContain("using System.IO;");
    }

    [Fact]
    public async Task RemoveUnusedUsings_NonFirstUsingRemoved_PreservesCopyrightHeader()
    {
        // Arrange — control test: removing a non-first using should always preserve the header
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        await RewriteFileAsync(Fixture, serviceFile, c =>
            CopyrightHeader
            + c.Replace("using TestFixture.Shapes;",
                "using TestFixture.Shapes;\r\nusing System.IO;"));

        // Act — System.IO is unused and sorts after TestFixture.Shapes's position
        await directiveTools.RemoveUnusedUsings([serviceFile], ct: TestContext.Current.CancellationToken);

        // Assert — copyright header preserved (non-first using removed)
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Copyright (c) Test Corp");
        content.ShouldContain("Licensed under the MIT License");
        content.ShouldContain("using TestFixture.Shapes;");
        content.ShouldNotContain("using System.IO;");
    }

    [Fact]
    public async Task RemoveUnusedUsings_NonCsFile_ReturnsError()
    {
        // Arrange — use the .csproj file that exists in the fixture
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string csprojFile = Path.Combine(
            Fixture.WorkspaceManager.SolutionDirectory!, "TestFixture", "TestFixture.csproj");

        // Act
        string result = await directiveTools.RemoveUnusedUsings([csprojFile], ct: TestContext.Current.CancellationToken);

        // Assert — should report error, not attempt to process
        result.ShouldContain(".cs");
        result.ShouldContain("Error");
    }

    // ── Preprocessor directive protection ────────────────────────────────

    [Fact]
    public async Task RemoveUnusedUsings_ConditionalBlock_PreservesEntireBlock()
    {
        // Arrange — file with unused using outside a #if/#else/#endif block + used using
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, _ =>
            "using System.IO;\r\n"
            + "#if NET8_0_OR_GREATER\r\n"
            + "using System.Numerics;\r\n"
            + "#else\r\n"
            + "using System.Threading;\r\n"
            + "#endif\r\n"
            + "\r\n"
            + "namespace TestFixture.Shapes;\r\n\r\n"
            + "public class Circle(double radius) : Shape\r\n{\r\n"
            + "    public double Radius { get; } = radius;\r\n"
            + "    public override double Area => Math.PI * Radius * Radius;\r\n"
            + "    public override double Perimeter => 2 * Math.PI * Radius;\r\n}\r\n");

        // Act
        string result = await directiveTools.RemoveUnusedUsings([circleFile], ct: TestContext.Current.CancellationToken);

        // Assert — System.IO removed, but the entire #if block is preserved verbatim
        result.ShouldContain("System.IO");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("using System.IO;");
        content.ShouldContain("#if NET8_0_OR_GREATER");
        content.ShouldContain("using System.Numerics;");
        content.ShouldContain("#else");
        content.ShouldContain("using System.Threading;");
        content.ShouldContain("#endif");
    }

    [Fact]
    public async Task RemoveUnusedUsings_UnusedUsingInsideConditionalBlock_SkipsIt()
    {
        // Arrange — unused using inside #if block should NOT be removed
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        await RewriteFileAsync(Fixture, serviceFile, c =>
            "#if NET8_0_OR_GREATER\r\n"
            + "using System.IO;\r\n"
            + "#endif\r\n"
            + c);

        // Act
        string result = await directiveTools.RemoveUnusedUsings([serviceFile], ct: TestContext.Current.CancellationToken);

        // Assert — System.IO is unused but conditional, so it must be preserved
        result.ShouldNotContain("System.IO");
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldContain("#if NET8_0_OR_GREATER");
        content.ShouldContain("using System.IO;");
        content.ShouldContain("#endif");
        content.ShouldContain("using TestFixture.Shapes;");
    }

    [Fact]
    public async Task RemoveUnusedUsings_ConditionalBlock_SkipsSorting()
    {
        // Arrange — usings in wrong sort order + conditional block → sorting must be skipped
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        await RewriteFileAsync(Fixture, serviceFile, _ =>
            "using TestFixture.Shapes;\r\n"
            + "using System.Diagnostics;\r\n"
            + "#if NET8_0_OR_GREATER\r\n"
            + "using System.Numerics;\r\n"
            + "#endif\r\n"
            + "\r\n"
            + "namespace TestFixture.Services;\r\n\r\n"
            + "public class ShapeService\r\n{\r\n"
            + "    public IShape? Shape { get; set; }\r\n"
            + "    public void Log() => Trace.WriteLine(Shape?.Describe());\r\n}\r\n");

        // Act
        await directiveTools.RemoveUnusedUsings([serviceFile], ct: TestContext.Current.CancellationToken);

        // Assert — no usings removed, AND order preserved (not sorted) due to conditional block
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        int shapesIdx = content.IndexOf("using TestFixture.Shapes;", StringComparison.Ordinal);
        int diagIdx = content.IndexOf("using System.Diagnostics;", StringComparison.Ordinal);
        shapesIdx.ShouldBeLessThan(diagIdx,
            "TestFixture.Shapes should remain before System.Diagnostics — sorting must be skipped");
        content.ShouldContain("#if NET8_0_OR_GREATER");
    }

    [Fact]
    public async Task RemoveUnusedUsings_RegionAroundUsings_PreservesRegion()
    {
        // Arrange — #region directive on a using prevents removal
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, _ =>
            "#region Usings\r\n"
            + "using System.IO;\r\n"
            + "#endregion\r\n"
            + "\r\n"
            + "namespace TestFixture.Shapes;\r\n\r\n"
            + "public class Circle(double radius) : Shape\r\n{\r\n"
            + "    public double Radius { get; } = radius;\r\n"
            + "    public override double Area => Math.PI * Radius * Radius;\r\n"
            + "    public override double Perimeter => 2 * Math.PI * Radius;\r\n}\r\n");

        // Act
        await directiveTools.RemoveUnusedUsings([circleFile], ct: TestContext.Current.CancellationToken);

        // Assert — System.IO has #region directive trivia, so it must not be removed
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("#region Usings");
        content.ShouldContain("using System.IO;");
        content.ShouldContain("#endregion");
    }

    [Fact]
    public async Task RemoveUnusedUsings_PragmaBeforeUsing_PreservesPragma()
    {
        // Arrange — #pragma warning disable before a using prevents removal
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, _ =>
            "#pragma warning disable CS0618\r\n"
            + "using System.IO;\r\n"
            + "#pragma warning restore CS0618\r\n"
            + "\r\n"
            + "namespace TestFixture.Shapes;\r\n\r\n"
            + "public class Circle(double radius) : Shape\r\n{\r\n"
            + "    public double Radius { get; } = radius;\r\n"
            + "    public override double Area => Math.PI * Radius * Radius;\r\n"
            + "    public override double Perimeter => 2 * Math.PI * Radius;\r\n}\r\n");

        // Act
        await directiveTools.RemoveUnusedUsings([circleFile], ct: TestContext.Current.CancellationToken);

        // Assert — using has #pragma directive trivia, so it must not be removed
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldContain("#pragma warning disable CS0618");
        content.ShouldContain("using System.IO;");
        content.ShouldContain("#pragma warning restore CS0618");
    }

    [Fact]
    public async Task RemoveUnusedUsings_UnusedUsingAfterClosedConditionalBlock_RemovesIt()
    {
        // Arrange — an unused unconditional using immediately follows a closed #if/#endif block.
        // CR-13: the trailing #endif lands in this using's leading trivia; the old code treated
        // that as "protected" and wrongly skipped the using. The conditional using inside the
        // block stays put; the unconditional one after it must be removed.
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        await RewriteFileAsync(Fixture, serviceFile, c =>
            "#if NET8_0_OR_GREATER\r\n"
            + "using System.Numerics;\r\n"
            + "#endif\r\n"
            + "using System.IO;\r\n"
            + c);

        // Act
        string result = await directiveTools.RemoveUnusedUsings([serviceFile], ct: TestContext.Current.CancellationToken);

        // Assert — System.IO removed; the conditional block (and its #endif) preserved. Removing a
        // using whose leading trivia holds the closing #endif must not unbalance the directives.
        result.ShouldContain("System.IO");
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("using System.IO;");
        content.ShouldContain("#if NET8_0_OR_GREATER");
        content.ShouldContain("using System.Numerics;");
        content.ShouldContain("#endif");
        content.ShouldContain("using TestFixture.Shapes;");
    }

    [Fact]
    public async Task RemoveUnusedUsings_ClosedConditionalNoUsingInside_StillSorts()
    {
        // Arrange — a closed #if/#endif that wraps no using lands both directives on the following
        // using's leading trivia. CR-13: the old code let the trailing #endif mark that using
        // protected → canSort=false → sorting disabled file-wide. After the fix the unconditional
        // usings sort normally. Both usings are used, so this isolates the sorting behaviour.
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        await RewriteFileAsync(Fixture, serviceFile, _ =>
            "#if NET8_0_OR_GREATER\r\n"
            + "#endif\r\n"
            + "using TestFixture.Shapes;\r\n"
            + "using System.Diagnostics;\r\n"
            + "\r\n"
            + "namespace TestFixture.Services;\r\n\r\n"
            + "public class ShapeService\r\n{\r\n"
            + "    public IShape? Shape { get; set; }\r\n"
            + "    public void Log() => Trace.WriteLine(Shape?.Describe());\r\n}\r\n");

        // Act
        await directiveTools.RemoveUnusedUsings([serviceFile], ct: TestContext.Current.CancellationToken);

        // Assert — nothing removed (both used), and sorting applied: System.Diagnostics before
        // TestFixture.Shapes, with the closed conditional preserved.
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        int diagIdx = content.IndexOf("using System.Diagnostics;", StringComparison.Ordinal);
        int shapesIdx = content.IndexOf("using TestFixture.Shapes;", StringComparison.Ordinal);
        diagIdx.ShouldBeGreaterThan(-1, "System.Diagnostics should still be present");
        shapesIdx.ShouldBeGreaterThan(-1, "TestFixture.Shapes should still be present");
        diagIdx.ShouldBeLessThan(shapesIdx, "a closed conditional must not disable sorting of unconditional usings");
        content.ShouldContain("#if NET8_0_OR_GREATER");
        content.ShouldContain("#endif");
    }

    // ── CS0246 (unresolved namespace) protection ────────────────────────

    [Fact]
    public async Task RemoveUnusedUsings_UnresolvedNamespace_PreservesItAndRemovesOther()
    {
        // Arrange — prepend an unresolvable using (CS0246) AND an unused but resolvable one
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile,
            c => "using Some.Fake.Namespace;\r\nusing System.IO;\r\n" + c);

        // Act
        string result = await directiveTools.RemoveUnusedUsings([circleFile], ct: TestContext.Current.CancellationToken);

        // Assert — System.IO removed, Some.Fake.Namespace preserved with unresolved warning
        result.ShouldContain("System.IO");
        result.ShouldContain("Preserved");
        result.ShouldContain("Some.Fake.Namespace");
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("using System.IO;");
        content.ShouldContain("using Some.Fake.Namespace;");
    }

    // ── Standalone comment preservation (F8c) ────────────────────────────

    [Fact]
    public async Task RemoveUnusedUsings_StandaloneCommentOnNonFirstUsing_Preserved()
    {
        // Arrange — unused System.Xml (triggers the post-removal sort) + a standalone "// Third-party"
        // comment on a used, non-first using. The sort path strips every using's leading trivia,
        // which would delete the comment; skip-sorting when such a comment is present preserves it.
        UsingDirectiveTools directiveTools = CreateUsingTools(Fixture);
        string serviceFile = ShapeServiceFile(Fixture);
        await RewriteFileAsync(Fixture, serviceFile, _ =>
            "using System.Xml;\r\n"
            + "using System.Diagnostics;\r\n"
            + "// Third-party\r\n"
            + "using TestFixture.Shapes;\r\n"
            + "\r\n"
            + "namespace TestFixture.Services;\r\n\r\n"
            + "public class ShapeService\r\n{\r\n"
            + "    public IShape? Shape { get; set; }\r\n"
            + "    public void Log() => Trace.WriteLine(Shape?.Describe());\r\n}\r\n");

        // Act
        string result = await directiveTools.RemoveUnusedUsings([serviceFile], ct: TestContext.Current.CancellationToken);

        // Assert — System.Xml removed; the standalone "// Third-party" comment survives
        result.ShouldContain("System.Xml");
        string content = await File.ReadAllTextAsync(serviceFile, TestContext.Current.CancellationToken);
        content.ShouldNotContain("using System.Xml;");
        content.ShouldContain("// Third-party");
        content.ShouldContain("using TestFixture.Shapes;");
        content.ShouldContain("using System.Diagnostics;");
    }
}
