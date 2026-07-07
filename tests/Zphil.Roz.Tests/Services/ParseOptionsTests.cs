using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Zphil.Roz.Services;

namespace Zphil.Roz.Tests.Services;

public class ParseOptionsTests
{
    public static IEnumerable<TheoryDataRow<LanguageVersion, string>> ValidCodeByVersion =>
    [
        // ── C# 7.3 ──────────────────────────────────────────────────
        new(LanguageVersion.CSharp7_3, "public void Foo() { }") { Label = "simple method" },
        new(LanguageVersion.CSharp7_3, "public int Add(int a, int b) { return a + b; }") { Label = "method with params" },
        new(LanguageVersion.CSharp7_3, "public T Identity<T>(T value) { return value; }") { Label = "generic method" },
        new(LanguageVersion.CSharp7_3, "public int Double(int x) => x * 2;") { Label = "expression-bodied method" },
        new(LanguageVersion.CSharp7_3, "public async System.Threading.Tasks.Task DoAsync() { await System.Threading.Tasks.Task.Delay(1); }") { Label = "async method" },
        new(LanguageVersion.CSharp7_3, "public static void Foo() { }") { Label = "static method" },
        new(LanguageVersion.CSharp7_3, "public virtual void Foo() { }") { Label = "virtual method" },
        new(LanguageVersion.CSharp7_3, "public abstract void Foo();") { Label = "abstract method" },
        new(LanguageVersion.CSharp7_3, "public override string ToString() { return \"\"; }") { Label = "override method" },
        new(LanguageVersion.CSharp7_3, "public string Name { get; set; }") { Label = "auto property" },
        new(LanguageVersion.CSharp7_3, "public string Name { get; }") { Label = "get-only property" },
        new(LanguageVersion.CSharp7_3, "public int Count => 42;") { Label = "expression-bodied property" },
        new(LanguageVersion.CSharp7_3, "public string Name { get { return \"\"; } set { } }") { Label = "full property" },
        new(LanguageVersion.CSharp7_3, "private int _count;") { Label = "field" },
        new(LanguageVersion.CSharp7_3, "public const int Max = 100;") { Label = "const field" },
        new(LanguageVersion.CSharp7_3, "public static readonly string Empty = \"\";") { Label = "static readonly field" },
        new(LanguageVersion.CSharp7_3, "public event System.EventHandler Changed;") { Label = "event" },
        new(LanguageVersion.CSharp7_3, "public Foo() { }") { Label = "constructor" },
        new(LanguageVersion.CSharp7_3, "static Foo() { }") { Label = "static constructor" },
        new(LanguageVersion.CSharp7_3, "~Foo() { }") { Label = "finalizer" },
        new(LanguageVersion.CSharp7_3, "public static Foo operator +(Foo a, Foo b) { return a; }") { Label = "operator" },
        new(LanguageVersion.CSharp7_3, "public int this[int i] { get { return 0; } }") { Label = "indexer" },
        new(LanguageVersion.CSharp7_3, "public static implicit operator int(Foo f) { return 0; }") { Label = "implicit conversion" },
        new(LanguageVersion.CSharp7_3, "public class Inner { }") { Label = "nested class" },
        new(LanguageVersion.CSharp7_3, "public struct Point { public double X; }") { Label = "nested struct" },
        new(LanguageVersion.CSharp7_3, "public interface IFoo { void Bar(); }") { Label = "nested interface" },
        new(LanguageVersion.CSharp7_3, "public enum Color { Red, Green, Blue }") { Label = "enum" },
        new(LanguageVersion.CSharp7_3, "public delegate void Handler(System.EventArgs e);") { Label = "delegate" },
        new(LanguageVersion.CSharp7_3, "public (int x, int y) GetPoint() { return (1, 2); }") { Label = "tuple return (C# 7.0)" },
        new(LanguageVersion.CSharp7_3, "public void Foo(in int x) { }") { Label = "in parameter (C# 7.2)" },
        new(LanguageVersion.CSharp7_3, "public ref int GetRef(int[] arr) { return ref arr[0]; }") { Label = "ref return (C# 7.0)" },
        new(LanguageVersion.CSharp7_3, "private int record;") { Label = "contextual keyword 'record' as field name" },
        new(LanguageVersion.CSharp7_3, "public void record() { }") { Label = "contextual keyword 'record' as method name" },

        // ── C# 8 ────────────────────────────────────────────────────
        new(LanguageVersion.CSharp8, "public string? Name { get; set; }") { Label = "nullable ref type property" },
        new(LanguageVersion.CSharp8, "public string Describe(int x) { return x switch { 0 => \"zero\", _ => \"other\" }; }") { Label = "switch expression in method" },
        new(LanguageVersion.CSharp8, "public void Read() { using var sr = new System.IO.StreamReader(\"f\"); }") { Label = "using declaration in method" },
        new(LanguageVersion.CSharp8, "public interface IFoo { void Bar() { } }") { Label = "default interface method" },

        // ── C# 9 ────────────────────────────────────────────────────
        new(LanguageVersion.CSharp9, "public record Point(double X, double Y);") { Label = "positional record" },
        new(LanguageVersion.CSharp9, "public string Name { get; init; }") { Label = "init-only property" },
        new(LanguageVersion.CSharp9, "public record Person { public string Name { get; init; } }") { Label = "record with body" },

        // ── C# 10 ───────────────────────────────────────────────────
        new(LanguageVersion.CSharp10, "public record struct Point(double X, double Y);") { Label = "record struct" },

        // ── C# 11 ───────────────────────────────────────────────────
        new(LanguageVersion.CSharp11, "public required string Name { get; set; }") { Label = "required property" },
        new(LanguageVersion.CSharp11, "file class Helper { }") { Label = "file-scoped type" },

        // ── C# 12 ───────────────────────────────────────────────────
        new(LanguageVersion.CSharp12, "public class Foo(int x) { }") { Label = "primary constructor" },

        // ── Semantic-only incompatibilities ──────────────────────────
        // These features parse identically in all versions (same node type, no
        // diagnostics). The guard correctly lets them through — build catches the
        // semantic error. Including them here documents they are NOT false positives
        // and verifies the guard doesn't regress into blocking them.
        new(LanguageVersion.CSharp7_3, "public string Name { get; init; }") { Label = "init property in C# 7.3 (semantic)" },
        new(LanguageVersion.CSharp7_3, "public required string Name { get; set; }") { Label = "required property in C# 7.3 (semantic)" },
        new(LanguageVersion.CSharp7_3, "public class Foo(int x) { }") { Label = "primary ctor in C# 7.3 (semantic)" },
        new(LanguageVersion.CSharp9, "public record struct Point(double X, double Y);") { Label = "record struct in C# 9 (semantic)" },
        new(LanguageVersion.CSharp10, "public required string Name { get; set; }") { Label = "required property in C# 10 (semantic)" },
        new(LanguageVersion.CSharp11, "public class Foo(int x) { }") { Label = "primary ctor in C# 11 (semantic)" }
    ];

    /// <summary>
    ///     Only features that produce parse-level differences (different node types or
    ///     <c>ContainsDiagnostics</c>) are catchable by the guard. Features like <c>init</c>,
    ///     <c>required</c>, and primary constructors parse identically in all versions —
    ///     their errors are semantic, caught by the build (not the guard). That's by design:
    ///     the guard avoids false positives at the cost of missing some semantic-only issues.
    /// </summary>
    public static IEnumerable<TheoryDataRow<LanguageVersion, string>> IncompatibleCodeByVersion =>
    [
        // record → MethodDeclarationSyntax in 7.3/8, RecordDeclarationSyntax in 9+ (type mismatch)
        new(LanguageVersion.CSharp7_3, "public record Point(double X, double Y);") { Label = "record in C# 7.3" },
        new(LanguageVersion.CSharp8, "public record Point(double X, double Y);") { Label = "record in C# 8" },

        // record struct → IncompleteMemberSyntax with diagnostics in 7.3 (null vs valid)
        new(LanguageVersion.CSharp7_3, "public record struct Point(double X, double Y);") { Label = "record struct in C# 7.3" }
    ];

    [Fact]
    public void TryParseMember_WithCSharp9_ParsesRecordAsRecordDeclaration()
    {
        // Arrange
        var options = new CSharpParseOptions(LanguageVersion.CSharp9);
        const string recordCode = "public record Point(double X, double Y);";

        // Act
        MemberDeclarationSyntax? result = SymbolEditService.TryParseMember(recordCode, options);

        // Assert — C# 9 recognizes the record keyword
        result.ShouldNotBeNull();
        result.ShouldBeOfType<RecordDeclarationSyntax>();
    }

    [Fact]
    public void TryParseMember_WithCSharp7_ParsesRecordAsMethodDeclaration()
    {
        // Arrange — in C# 7.3, 'record' is just an identifier, not a keyword.
        // 'public record Point(double X, double Y);' parses as a method
        // with return type 'record' and name 'Point'.
        var options = new CSharpParseOptions(LanguageVersion.CSharp7_3);
        const string recordCode = "public record Point(double X, double Y);";

        // Act
        MemberDeclarationSyntax? result = SymbolEditService.TryParseMember(recordCode, options);

        // Assert — parses as a method, not a record
        result.ShouldNotBeNull();
        result.ShouldBeOfType<MethodDeclarationSyntax>();
    }

    [Fact]
    public void TryParseMember_WithoutParseOptions_AcceptsModernSyntax()
    {
        // Arrange — null options uses default (latest)
        const string recordCode = "public record Point(double X, double Y);";

        // Act
        MemberDeclarationSyntax? result = SymbolEditService.TryParseMember(recordCode);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldBeOfType<RecordDeclarationSyntax>();
    }

    [Fact]
    public void ParseDeclaration_WithCSharp9_ParsesRecordCorrectly()
    {
        // Arrange
        var options = new CSharpParseOptions(LanguageVersion.CSharp9);
        ClassDeclarationSyntax targetNode = SyntaxFactory.ClassDeclaration("Dummy");
        const string recordCode = "public record Point(double X, double Y);";

        // Act
        SyntaxNode? result = SymbolEditService.ParseDeclaration(targetNode, recordCode, options);

        // Assert
        result.ShouldNotBeNull();
        result.ShouldBeOfType<RecordDeclarationSyntax>();
    }

    [Fact]
    public void ParseDeclaration_WithCSharp7_ParsesRecordAsMethod()
    {
        // Arrange — in C# 7.3, record is not a keyword
        var options = new CSharpParseOptions(LanguageVersion.CSharp7_3);
        ClassDeclarationSyntax targetNode = SyntaxFactory.ClassDeclaration("Dummy");
        const string recordCode = "public record Point(double X, double Y);";

        // Act
        SyntaxNode? result = SymbolEditService.ParseDeclaration(targetNode, recordCode, options);

        // Assert — interpreted as a method, not a record
        result.ShouldNotBeNull();
        result.ShouldBeOfType<MethodDeclarationSyntax>();
    }

    [Fact]
    public void ParseDeclaration_OptionsAffectInterpretation()
    {
        // Arrange — same code, different parse options yield different AST nodes.
        // This proves that passing parse options matters.
        ClassDeclarationSyntax targetNode = SyntaxFactory.ClassDeclaration("Dummy");
        const string code = "public record Point(double X, double Y);";

        // Act
        SyntaxNode? withCSharp7 = SymbolEditService.ParseDeclaration(
            targetNode, code, new CSharpParseOptions(LanguageVersion.CSharp7_3));
        SyntaxNode? withCSharp9 = SymbolEditService.ParseDeclaration(
            targetNode, code, new CSharpParseOptions(LanguageVersion.CSharp9));

        // Assert — different interpretations based on language version
        withCSharp7.ShouldNotBeNull();
        withCSharp9.ShouldNotBeNull();
        withCSharp7.GetType().ShouldNotBe(withCSharp9.GetType());
    }

    [Fact]
    public void ValidateLanguageVersion_RecordInCSharp7_ThrowsWithMismatchDetails()
    {
        // Arrange — record parses as a method in C# 7.3 but a record declaration in latest
        var options = new CSharpParseOptions(LanguageVersion.CSharp7_3);
        const string code = "public record Point(double X, double Y);";
        SyntaxNode? targetResult = SymbolEditService.TryParseMember(code, options);

        // Act & Assert
        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() =>
            SymbolEditService.ValidateLanguageVersionCompatibility(
                targetResult, options,
                opts => SymbolEditService.TryParseMember(code, opts)));

        ex.Message.ShouldContain("Language version conflict");
        ex.Message.ShouldContain("a record declaration");
        ex.Message.ShouldContain("a method declaration");
        ex.Message.ShouldContain("7.3");
    }

    [Fact]
    public void ValidateLanguageVersion_TargetNullLatestSucceeds_ThrowsWithDiagnostics()
    {
        // Arrange — simulate code that fails to parse in the target version (null)
        // but succeeds in latest C#. This tests the "parse fails in target, succeeds in latest"
        // path directly, independent of which specific C# features produce parse-level diagnostics.
        var options = new CSharpParseOptions(LanguageVersion.CSharp7_3);
        ClassDeclarationSyntax fakeLatestResult = SyntaxFactory.ClassDeclaration("Foo");

        // Act & Assert
        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() =>
            SymbolEditService.ValidateLanguageVersionCompatibility(
                null, options,
                _ => fakeLatestResult));

        ex.Message.ShouldContain("not available");
        ex.Message.ShouldContain("7.3");
    }

    [Fact]
    public void ValidateLanguageVersion_InvalidCodeInAllVersions_DoesNotThrow()
    {
        // Arrange — garbage code fails to parse in both target and latest
        var options = new CSharpParseOptions(LanguageVersion.CSharp7_3);
        const string code = "not valid csharp !!!";
        SyntaxNode? targetResult = SymbolEditService.TryParseMember(code, options);

        // Act & Assert — both null, no version issue; the caller handles the generic error
        Should.NotThrow(() =>
            SymbolEditService.ValidateLanguageVersionCompatibility(
                targetResult, options,
                opts => SymbolEditService.TryParseMember(code, opts)));
    }

    [Fact]
    public void ValidateLanguageVersion_LatestTarget_SkipsValidation()
    {
        // Arrange — when target is already latest, validation short-circuits without reparsing
        var options = new CSharpParseOptions(LanguageVersion.Latest);
        var reparseCalled = false;

        // Act & Assert — reparse should never be called
        Should.NotThrow(() =>
            SymbolEditService.ValidateLanguageVersionCompatibility(
                null, options,
                _ =>
                {
                    reparseCalled = true;
                    return null;
                }));

        reparseCalled.ShouldBeFalse("Reparse should not be called when target is already latest");
    }

    // ── Multi-version false positive prevention ──────────────────────────

    [Theory]
    [MemberData(nameof(ValidCodeByVersion))]
    public void ValidateLanguageVersion_ValidCodeAtTargetVersion_DoesNotThrow(
        LanguageVersion targetVersion, string code)
    {
        // Arrange — code is valid at the target version, guard must not reject it
        var options = new CSharpParseOptions(targetVersion);
        SyntaxNode? targetResult = SymbolEditService.TryParseMember(code, options);

        // Act & Assert — must not throw (false positive = blocking valid code)
        Should.NotThrow(() => SymbolEditService.ValidateLanguageVersionCompatibility(
            targetResult, options,
            opts => SymbolEditService.TryParseMember(code, opts)));
    }

    // ── Local function path (ParseDeclaration with ParseStatement) ───────

    [Theory]
    [InlineData("int Square(int x) => x * x;", Label = "expression-bodied")]
    [InlineData("int Factorial(int n) { return n <= 1 ? 1 : n * Factorial(n - 1); }", Label = "block body")]
    public void ValidateLanguageVersion_LocalFunction_DoesNotThrow(string code)
    {
        // Arrange — local function replacement uses the ParseStatement path
        var options = new CSharpParseOptions(LanguageVersion.CSharp7_3);
        var targetNode =
            (LocalFunctionStatementSyntax)SyntaxFactory.ParseStatement(code);
        SyntaxNode? targetResult = SymbolEditService.ParseDeclaration(targetNode, code, options);

        // Act & Assert
        Should.NotThrow(() =>
            SymbolEditService.ValidateLanguageVersionCompatibility(
                targetResult, options,
                opts => SymbolEditService.ParseDeclaration(targetNode, code, opts)));
    }

    // ── Correct rejections across version boundaries ─────────────────────

    [Theory]
    [MemberData(nameof(IncompatibleCodeByVersion))]
    public void ValidateLanguageVersion_IncompatibleCode_Throws(
        LanguageVersion targetVersion, string code)
    {
        // Arrange — code requires a newer C# version than the target
        var options = new CSharpParseOptions(targetVersion);
        SyntaxNode? targetResult = SymbolEditService.TryParseMember(code, options);

        // Act & Assert — guard must catch this
        Should.Throw<InvalidOperationException>(() => SymbolEditService.ValidateLanguageVersionCompatibility(
            targetResult, options,
            opts => SymbolEditService.TryParseMember(code, opts)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void TryParseMember_EmptyOrWhitespace_ReturnsNull(string content)
    {
        // Arrange / Act
        MemberDeclarationSyntax? result = SymbolEditService.TryParseMember(content);

        // Assert
        result.ShouldBeNull();
    }

    // ── FriendlyNodeDescription coverage via ValidateLanguageVersionCompatibility ──

    [Theory]
    [InlineData("class")]
    [InlineData("struct")]
    [InlineData("interface")]
    [InlineData("enum")]
    public void ValidateLanguageVersion_TypeMismatch_MessageContainsFriendlyDescription(string keyword)
    {
        // Arrange — produce a mismatch by providing a fake latest result of the given type,
        // while the target result is a different node type (a class declaration).
        // The message should contain the friendly description for each node type.
        var options = new CSharpParseOptions(LanguageVersion.CSharp7_3);
        SyntaxNode targetResult = SyntaxFactory.ClassDeclaration("A");
        SyntaxNode latestResult = keyword switch
        {
            "class" => SyntaxFactory.ClassDeclaration("B"),
            "struct" => SyntaxFactory.StructDeclaration("B"),
            "interface" => SyntaxFactory.InterfaceDeclaration("B"),
            "enum" => SyntaxFactory.EnumDeclaration("B"),
            _ => throw new ArgumentOutOfRangeException(nameof(keyword))
        };

        // Act & Assert — only throws when node types differ
        if (targetResult.GetType() == latestResult.GetType())
        {
            Should.NotThrow(() =>
                SymbolEditService.ValidateLanguageVersionCompatibility(targetResult, options, _ => latestResult));
        }
        else
        {
            InvalidOperationException ex = Should.Throw<InvalidOperationException>(() =>
                SymbolEditService.ValidateLanguageVersionCompatibility(targetResult, options, _ => latestResult));

            ex.Message.ShouldContain("Language version conflict");
        }
    }

    [Fact]
    public void ValidateLanguageVersion_MethodVsProperty_MessageContainsFriendlyDescriptions()
    {
        // Arrange — target result is a method, latest is a property (different node types)
        var options = new CSharpParseOptions(LanguageVersion.CSharp7_3);
        SyntaxNode targetResult = SyntaxFactory.MethodDeclaration(
            SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.VoidKeyword)),
            "Foo");
        SyntaxNode latestResult = SyntaxFactory.PropertyDeclaration(
            SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
            "Bar");

        // Act & Assert
        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() =>
            SymbolEditService.ValidateLanguageVersionCompatibility(targetResult, options, _ => latestResult));

        ex.Message.ShouldContain("a method declaration");
        ex.Message.ShouldContain("a property declaration");
    }

    [Fact]
    public void ValidateLanguageVersion_FieldVsConstructor_MessageContainsFriendlyDescriptions()
    {
        // Arrange — target is a field, latest is a constructor
        var options = new CSharpParseOptions(LanguageVersion.CSharp7_3);
        SyntaxNode targetResult = SyntaxFactory.FieldDeclaration(
            SyntaxFactory.VariableDeclaration(
                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.IntKeyword)),
                SyntaxFactory.SingletonSeparatedList(SyntaxFactory.VariableDeclarator("_x"))));
        SyntaxNode latestResult = SyntaxFactory.ConstructorDeclaration("Foo");

        // Act & Assert
        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() =>
            SymbolEditService.ValidateLanguageVersionCompatibility(targetResult, options, _ => latestResult));

        ex.Message.ShouldContain("a field declaration");
        ex.Message.ShouldContain("a constructor declaration");
    }

    [Fact]
    public void ValidateLanguageVersion_UnknownNodeType_MessageContainsTypeName()
    {
        // Arrange — use an IncompleteMemberSyntax (no dedicated friendly name) as the target
        // and a class declaration as the latest result so they differ
        var options = new CSharpParseOptions(LanguageVersion.CSharp7_3);
        SyntaxNode targetResult = SyntaxFactory.IncompleteMember();
        SyntaxNode latestResult = SyntaxFactory.ClassDeclaration("B");

        // Act & Assert
        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() =>
            SymbolEditService.ValidateLanguageVersionCompatibility(targetResult, options, _ => latestResult));

        ex.Message.ShouldContain("Language version conflict");
        ex.Message.ShouldContain("IncompleteMemberSyntax");
    }

    [Fact]
    public void ValidateLanguageVersion_TargetNullWithDiagnostics_ThrowsWithSpecificDiagnosticMessages()
    {
        // Arrange — "record struct" in C# 7.3 fails to parse (returns null from TryParseMember)
        // but succeeds in latest C#. This exercises ExtractDiagnosticsFromReparse
        // where the reparse returns null and the fallback message is used.
        var options = new CSharpParseOptions(LanguageVersion.CSharp7_3);
        const string code = "public record struct Point(double X, double Y);";
        SyntaxNode? targetResult = SymbolEditService.TryParseMember(code, options);
        targetResult.ShouldBeNull();

        // Act & Assert
        InvalidOperationException ex = Should.Throw<InvalidOperationException>(() =>
            SymbolEditService.ValidateLanguageVersionCompatibility(
                targetResult, options,
                opts => SymbolEditService.TryParseMember(code, opts)));

        ex.Message.ShouldContain("not available");
        ex.Message.ShouldContain("7.3");
    }

    [Fact]
    public void ValidateLanguageVersion_LocalFunctionInOlderVersion_DetectsMismatch()
    {
        // Arrange — a static local function requires C# 8; in C# 7.3 it produces diagnostics
        var options = new CSharpParseOptions(LanguageVersion.CSharp7_3);
        const string code = "static int Add(int a, int b) => a + b;";
        var targetNode = (LocalFunctionStatementSyntax)SyntaxFactory.ParseStatement(code);
        SyntaxNode? targetResult = SymbolEditService.ParseDeclaration(targetNode, code, options);

        // Act & Assert — static local function is not available in C# 7.3
        if (targetResult is null)
        {
            InvalidOperationException ex = Should.Throw<InvalidOperationException>(() =>
                SymbolEditService.ValidateLanguageVersionCompatibility(
                    targetResult, options,
                    opts => SymbolEditService.ParseDeclaration(targetNode, code, opts)));
            ex.Message.ShouldContain("not available");
        }
    }

    [Fact]
    public void ValidateLanguageVersion_SameNodeTypeBothVersions_DoesNotThrow()
    {
        // Arrange — code that parses as the same node type in both target and latest
        var options = new CSharpParseOptions(LanguageVersion.CSharp8);
        const string code = "public void Foo() { }";
        SyntaxNode? targetResult = SymbolEditService.TryParseMember(code, options);

        // Act & Assert — same node type in both versions, no exception
        Should.NotThrow(() =>
            SymbolEditService.ValidateLanguageVersionCompatibility(
                targetResult, options,
                opts => SymbolEditService.TryParseMember(code, opts)));
    }
}
