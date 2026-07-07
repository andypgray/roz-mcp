using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Verifies that replace_symbol, replace enum member, and insert enum member
///     normalize the indentation of caller-provided content to match the file context,
///     regardless of the caller's indentation.
/// </summary>
public class IndentationNormalizationTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    // ── replace_symbol ───────────────────────────────────────────────────

    [Fact]
    public async Task ReplaceSymbol_ZeroIndentedMultiLineBody_NormalizedToFileIndent()
    {
        // Arrange — Shape.cs has 4-space indented members (file-scoped namespace)
        string shapeFile = ShapeFile(Fixture);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace Describe with a zero-indented multi-line method
        string newDeclaration =
            "public virtual string Describe()\r\n"
            + "{\r\n"
            + "return $\"{GetType().Name}: Area={Area:F2}\";\r\n"
            + "}";
        await tools.ReplaceSymbol(shapeFile, "Describe", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — all lines should be at 4-space indent (matching the class body)
        string content = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int describeLine = lines.ShouldContainLine(l => l.Contains("Describe()"));
        lines[describeLine].ShouldStartWith("    public virtual string Describe()");
        lines[describeLine + 1].ShouldBe("    {");
        lines[describeLine + 2].ShouldBe("        return $\"{GetType().Name}: Area={Area:F2}\";");
        lines[describeLine + 3].ShouldBe("    }");
    }

    [Fact]
    public async Task ReplaceSymbol_OverIndentedMultiLineBody_NormalizedToFileIndent()
    {
        // Arrange
        string shapeFile = ShapeFile(Fixture);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace Describe with 12-space indented content
        string newDeclaration =
            "public virtual string Describe()\r\n"
            + "            {\r\n"
            + "                return $\"{GetType().Name}: Area={Area:F2}\";\r\n"
            + "            }";
        await tools.ReplaceSymbol(shapeFile, "Describe", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — normalized to 4-space indent
        string content = await File.ReadAllTextAsync(shapeFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int describeLine = lines.ShouldContainLine(l => l.Contains("Describe()"));
        lines[describeLine].ShouldStartWith("    public virtual string Describe()");
        lines[describeLine + 1].ShouldBe("    {");
        lines[describeLine + 2].ShouldBe("        return $\"{GetType().Name}: Area={Area:F2}\";");
        lines[describeLine + 3].ShouldBe("    }");
    }

    [Fact]
    public async Task ReplaceSymbol_LegacyNamespace_NormalizedToEightSpaceIndent()
    {
        // Arrange — LegacyClass.cs has traditional namespace { } with 8-space member indent
        string legacyFile = LegacyClassFile(Fixture);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace DoWork with zero-indented multi-line method
        string newDeclaration =
            "public void DoWork()\r\n"
            + "{\r\n"
            + "Console.WriteLine(\"updated\");\r\n"
            + "Console.WriteLine(Name);\r\n"
            + "}";
        await tools.ReplaceSymbol(legacyFile, "DoWork", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — all lines at 8-space indent (traditional namespace + class body)
        string content = await File.ReadAllTextAsync(legacyFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int doWorkLine = lines.ShouldContainLine(l => l.Contains("DoWork()"));
        lines[doWorkLine].ShouldStartWith("        public void DoWork()");
        lines[doWorkLine + 1].ShouldBe("        {");
        lines[doWorkLine + 2].ShouldBe("            Console.WriteLine(\"updated\");");
        lines[doWorkLine + 3].ShouldBe("            Console.WriteLine(Name);");
        lines[doWorkLine + 4].ShouldBe("        }");
    }

    [Fact]
    public async Task ReplaceSymbol_ZeroIndentedWithAttribute_AttributeNormalized()
    {
        // Arrange — add an attribute above Radius in Circle.cs
        string circleFile = CircleFile(Fixture);
        await RewriteFileAsync(Fixture, circleFile, content =>
            content.Replace(
                "    public double Radius",
                "    [System.ComponentModel.Description(\"The radius\")]\r\n    public double Radius"));

        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace Radius with zero-indented attribute + property
        string newDeclaration =
            "[System.ComponentModel.Description(\"The updated radius\")]\r\n"
            + "public double Radius { get; } = radius;";
        await tools.ReplaceSymbol(circleFile, "Radius", newDeclaration, ct: TestContext.Current.CancellationToken);

        // Assert — attribute and property at 4-space indent (not column 0)
        string content = await File.ReadAllTextAsync(circleFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int attrLine = lines.ShouldContainLine(l => l.Contains("Description("));
        lines[attrLine].ShouldStartWith("    [");
        int radiusLine = lines.ShouldContainLine(l => l.Contains("public double Radius"));
        lines[radiusLine].ShouldStartWith("    public");
    }

    // ── replace enum member ──────────────────────────────────────────────

    [Fact]
    public async Task ReplaceEnumMember_WithExplicitValue_NormalizedToFileIndent()
    {
        // Arrange — ShapeColor enum in TypeKindExamples.cs has 4-space indented members
        string typeKindFile = TypeKindExamplesFile(Fixture);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — replace Red with a new value
        await tools.ReplaceSymbol(typeKindFile, "Red", "Crimson = 10", ct: TestContext.Current.CancellationToken);

        // Assert — Crimson at 4-space indent
        string content = await File.ReadAllTextAsync(typeKindFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int crimsonLine = lines.ShouldContainLine(l => l.Contains("Crimson"));
        lines[crimsonLine].ShouldStartWith("    ");
    }

    // ── insert enum member ───────────────────────────────────────────────

    [Fact]
    public async Task InsertEnumMember_AfterExistingMember_NormalizedToFileIndent()
    {
        // Arrange — ShapeColor enum in TypeKindExamples.cs has 4-space indented members
        string typeKindFile = TypeKindExamplesFile(Fixture);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert a new enum member after Blue
        await tools.InsertSymbol(typeKindFile, "Blue", "Purple", ct: TestContext.Current.CancellationToken);

        // Assert — Purple at same 4-space indent as siblings
        string content = await File.ReadAllTextAsync(typeKindFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);
        int purpleLine = lines.ShouldContainLine(l => l.Contains("Purple"));
        lines[purpleLine].ShouldStartWith("    ");
    }

    [Fact]
    public async Task InsertEnumMember_MultipleMembers_AllNormalizedToFileIndent()
    {
        // Arrange
        string typeKindFile = TypeKindExamplesFile(Fixture);
        CodeEditTools tools = CreateEditTools(Fixture);

        // Act — insert multiple enum members after Yellow
        await tools.InsertSymbol(typeKindFile, "Yellow", "Orange,\r\nPurple,\r\nCyan", ct: TestContext.Current.CancellationToken);

        // Assert — all new members at 4-space indent
        string content = await File.ReadAllTextAsync(typeKindFile, TestContext.Current.CancellationToken);
        string[] lines = SplitLines(content);

        int orangeLine = lines.ShouldContainLine(l => l.Contains("Orange"));
        lines[orangeLine].ShouldStartWith("    ");

        int purpleLine = lines.ShouldContainLine(l => l.Contains("Purple"));
        lines[purpleLine].ShouldStartWith("    ");

        int cyanLine = lines.ShouldContainLine(l => l.Contains("Cyan"));
        lines[cyanLine].ShouldStartWith("    ");
    }

    // ── string-literal interiors (F5) ────────────────────────────────────

    [Fact]
    public async Task ReplaceSymbol_TabIndentedFile_PreservesStringLiteralInteriors()
    {
        // Arrange — StringLiteralIndent.cs is TAB-indented; Verbatim() returns a multi-line verbatim
        // literal whose interior lines are SPACE-indented (value-bearing). Replacing the method makes
        // the formatter emit SPACE indentation, so NormalizeInsertedIndentationAsync prefix-swaps
        // spaces->tabs to restore the file's tab style (insertedIndent != targetIndent — the case the
        // method exists to handle). Without the F5 fix that swap also rewrites the literal interior,
        // turning "        SELECT *" into "\t\tSELECT *" — silently changing the string value.
        string file = StringLiteralIndentFile(Fixture);
        CodeEditTools tools = CreateEditTools(Fixture);

        const string newDecl =
            "public string Verbatim()\r\n"
            + "{\r\n"
            + "return @\"\r\n"
            + "        SELECT *\r\n"
            + "          FROM Table\r\n"
            + "         WHERE Id = 1\";\r\n"
            + "}";
        await tools.ReplaceSymbol(file, "Verbatim", newDecl, ct: TestContext.Current.CancellationToken);

        // Assert — the verbatim literal interior keeps its original SPACE indentation (no tab
        // corruption). Raw-string interiors are covered by the same string-literal-token skip.
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("        SELECT *"); // 8 spaces, no leading tab
        content.ShouldContain("          FROM Table"); // 10 spaces
        content.ShouldContain("         WHERE Id = 1"); // 9 spaces
    }
}
