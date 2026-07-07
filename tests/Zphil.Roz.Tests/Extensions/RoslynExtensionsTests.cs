using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Models;

namespace Zphil.Roz.Tests.Extensions;

public class RoslynExtensionsTests
{
    // ── GetPosition validation ──────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void GetPosition_LineBelow1_Throws(int line)
    {
        var text = SourceText.From("class Foo { }");

        UserErrorException ex = Should.Throw<UserErrorException>(() => text.GetPosition(line, 1));

        ex.Message.ShouldContain("Line must be >= 1");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void GetPosition_ColumnBelow1_Throws(int column)
    {
        var text = SourceText.From("class Foo { }");

        UserErrorException ex = Should.Throw<UserErrorException>(() => text.GetPosition(1, column));

        ex.Message.ShouldContain("Column must be >= 1");
    }

    [Fact]
    public void GetPosition_LinePastEnd_Throws()
    {
        var text = SourceText.From("class Foo { }");

        UserErrorException ex = Should.Throw<UserErrorException>(() => text.GetPosition(999, 1));

        ex.Message.ShouldContain("out of range");
    }

    [Fact]
    public void GetPosition_ColumnPastEndOfLine_Throws()
    {
        var text = SourceText.From("class Foo { }");

        UserErrorException ex = Should.Throw<UserErrorException>(() => text.GetPosition(1, 999));

        ex.Message.ShouldContain("past end of line");
    }

    [Fact]
    public void GetPosition_ValidPosition_ReturnsCorrectOffset()
    {
        var text = SourceText.From("class Foo { }");

        // Line 1, column 7 → 0-based offset 6 → 'F' in 'Foo'
        int position = text.GetPosition(1, 7);

        position.ShouldBe(6);
    }

    // ── ClassifyPosition ────────────────────────────────────────────────────

    [Theory]
    [InlineData("// this is a comment\nclass Foo { }", 5, (int)PositionKind.Comment, Label = "single-line comment")]
    [InlineData("/* comment */\nclass Foo { }", 5, (int)PositionKind.Comment, Label = "multi-line comment")]
    [InlineData("/// <summary>Doc</summary>\nclass Foo { }", 5, (int)PositionKind.DocComment, Label = "doc comment")]
    [InlineData("class   Foo { }", 6, (int)PositionKind.Whitespace, Label = "whitespace between tokens")]
    [InlineData("class Foo { string x = \"hello\"; }", 25, (int)PositionKind.StringLiteral, Label = "string literal")]
    [InlineData("public class Foo { }", 0, (int)PositionKind.Keyword, Label = "keyword")]
    [InlineData("class Foo { }", 10, (int)PositionKind.Punctuation, Label = "punctuation")]
    [InlineData("class Foo { int x = 42; }", 20, (int)PositionKind.NumericLiteral, Label = "numeric literal")]
    public void ClassifyPosition_ReturnsExpectedKind(string source, int position, int expectedKindInt)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken);

        PositionClassification result = RoslynExtensions.ClassifyPosition(tree, position);

        result.Kind.ShouldBe((PositionKind)expectedKindInt);
    }

    [Fact]
    public void ClassifyPosition_DocComment_IsCommentReturnsTrue()
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText("/// <summary>Doc</summary>\nclass Foo { }", cancellationToken: TestContext.Current.CancellationToken);

        PositionClassification result = RoslynExtensions.ClassifyPosition(tree, 5);

        result.IsComment.ShouldBeTrue();
    }

    // ── ResolveFilePath validation ─────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveFilePath_NullOrWhiteSpace_Throws(string? filePath)
    {
        ArgumentException ex = Should.Throw<ArgumentException>(() => PathExtensions.ResolveFilePath(filePath!, @"C:\solution"));

        ex.ParamName.ShouldBe("filePath");
    }

    [Theory]
    // obj/ directory
    [InlineData(@"C:\project\obj\Debug\net8.0\App.GlobalUsings.g.cs", true)]
    [InlineData(@"C:\project\obj\Debug\net8.0\App.AssemblyInfo.cs", true)]
    [InlineData(@"C:\project\obj\Release\net8.0\Views\Home\Index.cshtml.g.cs", true)]
    [InlineData(@"C:/project/obj/Debug/net8.0/Generated.cs", true)]
    // .g.cs / .g.i.cs suffixes
    [InlineData(@"C:\project\Pages\Index.razor.g.cs", true)]
    [InlineData(@"C:\project\Generated.g.i.cs", true)]
    // .Designer.cs (WinForms, resx)
    [InlineData(@"C:\project\Form1.Designer.cs", true)]
    [InlineData(@"C:\project\Resources.Designer.cs", true)]
    [InlineData(@"C:\project\Form1.designer.cs", true)]
    // .generated.cs (T4 templates)
    [InlineData(@"C:\project\MyService.generated.cs", true)]
    // TemporaryGeneratedFile_ prefix
    [InlineData(@"C:\project\TemporaryGeneratedFile_ABC123.cs", true)]
    [InlineData(@"C:\project\temporarygeneratedfile_abc.cs", true)]
    // Normal source files
    [InlineData(@"C:\project\src\Controllers\HomeController.cs", false)]
    [InlineData(@"C:\project\Models\Customer.cs", false)]
    [InlineData(@"C:\project\objective\File.cs", false)]
    [InlineData(@"C:\project\Designer.cs", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsGeneratedFile_VariousPaths_ReturnsExpected(string? path, bool expected) => ProjectExtensions.IsGeneratedFile(path).ShouldBe(expected);

    // ── GetRelativePath ─────────────────────────────────────────────────────

    [Fact]
    public void GetRelativePath_EmptyFilePath_ReturnsNoFile()
    {
        // Arrange — a location with no associated file (e.g. a metadata-only location)
        Location location = Location.None;

        // Act
        string result = location.GetRelativePath(@"C:\solution");

        // Assert
        result.ShouldBe("(no file)");
    }

    // ── GetAccessibilityString ──────────────────────────────────────────────

    [Fact]
    public void GetAccessibilityString_ProtectedOrInternal_ReturnsProtectedInternal()
    {
        // Arrange — synthesise a compilation with a protected internal member
        SyntaxTree tree = CSharpSyntaxTree.ParseText("""
                                                     public class Base
                                                     {
                                                         protected internal void M() { }
                                                     }
                                                     """, cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("Test",
            [tree], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        INamedTypeSymbol? baseType = compilation.GetTypeByMetadataName("Base");
        IMethodSymbol method = baseType!.GetMembers("M").OfType<IMethodSymbol>().Single();

        // Act
        string result = method.GetAccessibilityString();

        // Assert
        result.ShouldBe("protected internal");
    }

    [Fact]
    public void GetAccessibilityString_PrivateProtected_ReturnsPrivateProtected()
    {
        // Arrange — synthesise a compilation with a private protected member
        SyntaxTree tree = CSharpSyntaxTree.ParseText("""
                                                     public class Base
                                                     {
                                                         private protected void M() { }
                                                     }
                                                     """, cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("Test",
            [tree], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        INamedTypeSymbol? baseType = compilation.GetTypeByMetadataName("Base");
        IMethodSymbol method = baseType!.GetMembers("M").OfType<IMethodSymbol>().Single();

        // Act
        string result = method.GetAccessibilityString();

        // Assert
        result.ShouldBe("private protected");
    }

    // ── GetKindString ───────────────────────────────────────────────────────

    [Fact]
    public void GetKindString_LocalSymbol_ReturnsLocal()
    {
        // Arrange — get a local variable symbol via semantic model
        SyntaxTree tree = CSharpSyntaxTree.ParseText("""
                                                     class C
                                                     {
                                                         void M()
                                                         {
                                                             int x = 42;
                                                             _ = x;
                                                         }
                                                     }
                                                     """, cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("Test",
            [tree], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        SemanticModel model = compilation.GetSemanticModel(tree);
        SyntaxNode root = tree.GetRoot(TestContext.Current.CancellationToken);
        VariableDeclaratorSyntax declarator = root.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .First(d => d.Identifier.Text == "x");
        ISymbol? local = model.GetDeclaredSymbol(declarator, TestContext.Current.CancellationToken);

        // Act
        string result = local!.GetKindString();

        // Assert
        result.ShouldBe("local");
    }

    [Fact]
    public void GetKindString_ParameterSymbol_ReturnsParameter()
    {
        // Arrange — get a parameter symbol via semantic model
        SyntaxTree tree = CSharpSyntaxTree.ParseText("""
                                                     class C
                                                     {
                                                         void M(int param) { }
                                                     }
                                                     """, cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("Test",
            [tree], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        SemanticModel model = compilation.GetSemanticModel(tree);
        SyntaxNode root = tree.GetRoot(TestContext.Current.CancellationToken);
        ParameterSyntax paramSyntax = root.DescendantNodes().OfType<ParameterSyntax>().First();
        ISymbol? param = model.GetDeclaredSymbol(paramSyntax, TestContext.Current.CancellationToken);

        // Act
        string result = param!.GetKindString();

        // Assert
        result.ShouldBe("parameter");
    }

    // ── IsUserVisibleMember ─────────────────────────────────────────────────

    [Fact]
    public void IsUserVisibleMember_ExplicitInterfaceEvent_ReturnsTrue()
    {
        // Arrange — explicit event interface implementation is user-visible
        SyntaxTree tree = CSharpSyntaxTree.ParseText("""
                                                     using System;
                                                     interface INotify { event EventHandler Changed; }
                                                     class Widget : INotify
                                                     {
                                                         event EventHandler INotify.Changed { add { } remove { } }
                                                     }
                                                     """, cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("Test",
            [tree], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        INamedTypeSymbol? widget = compilation.GetTypeByMetadataName("Widget");
        IEventSymbol eventSymbol = widget!.GetMembers()
            .OfType<IEventSymbol>()
            .First(e => !e.IsImplicitlyDeclared);

        // Act
        bool result = eventSymbol.IsUserVisibleMember();

        // Assert
        result.ShouldBeTrue();
    }

    // ── MatchesKindFilter ───────────────────────────────────────────────────

    [Fact]
    public void MatchesKindFilter_Namespace_MatchesNamespaceSymbol()
    {
        // Arrange — get a namespace symbol via compilation
        SyntaxTree tree = CSharpSyntaxTree.ParseText("namespace Foo { }", cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("Test",
            [tree], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        INamespaceSymbol ns = compilation.GlobalNamespace
            .GetNamespaceMembers()
            .First(n => n.Name == "Foo");

        // Act
        bool result = ns.MatchesKindFilter(SymbolicKind.Namespace);

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void MatchesKindFilter_Namespace_DoesNotMatchClassFilter()
    {
        // Arrange
        SyntaxTree tree = CSharpSyntaxTree.ParseText("namespace Foo { }", cancellationToken: TestContext.Current.CancellationToken);
        var compilation = CSharpCompilation.Create("Test",
            [tree], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        INamespaceSymbol ns = compilation.GlobalNamespace
            .GetNamespaceMembers()
            .First(n => n.Name == "Foo");

        // Act — namespace should not match Class filter
        bool result = ns.MatchesKindFilter(SymbolicKind.Class);

        // Assert
        result.ShouldBeFalse();
    }

    // ── GetIdentifierText ───────────────────────────────────────────────────

    [Fact]
    public void GetIdentifierText_EventDeclaration_ReturnsIdentifier()
    {
        // Arrange
        // For field-style events EventDeclarationSyntax won't appear; use EventFieldDeclarationSyntax instead.
        // Use an explicit event with accessors to get EventDeclarationSyntax.
        SyntaxTree treeExplicit = CSharpSyntaxTree.ParseText("""
                                                             using System;
                                                             interface INotify { event EventHandler Changed; }
                                                             class C : INotify
                                                             {
                                                                 event EventHandler INotify.Changed { add { } remove { } }
                                                             }
                                                             """, cancellationToken: TestContext.Current.CancellationToken);
        EventDeclarationSyntax eventDecl = treeExplicit.GetRoot(TestContext.Current.CancellationToken).DescendantNodes()
            .OfType<EventDeclarationSyntax>()
            .First();

        // Act
        string? result = eventDecl.GetIdentifierText();

        // Assert
        result.ShouldBe("Changed");
    }

    [Fact]
    public void GetIdentifierText_DelegateDeclaration_ReturnsIdentifier()
    {
        // Arrange
        SyntaxTree tree = CSharpSyntaxTree.ParseText("delegate void Handler();", cancellationToken: TestContext.Current.CancellationToken);
        DelegateDeclarationSyntax delegateDecl = tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes()
            .OfType<DelegateDeclarationSyntax>()
            .First();

        // Act
        string? result = delegateDecl.GetIdentifierText();

        // Assert
        result.ShouldBe("Handler");
    }

    [Fact]
    public void GetIdentifierText_EnumMember_ReturnsIdentifier()
    {
        // Arrange
        SyntaxTree tree = CSharpSyntaxTree.ParseText("enum Color { Red, Green }", cancellationToken: TestContext.Current.CancellationToken);
        EnumMemberDeclarationSyntax member = tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes()
            .OfType<EnumMemberDeclarationSyntax>()
            .First();

        // Act
        string? result = member.GetIdentifierText();

        // Assert
        result.ShouldBe("Red");
    }

    [Fact]
    public void GetIdentifierText_ConstructorDeclaration_ReturnsNull()
    {
        // Arrange — constructors have special Roslyn names (.ctor), not the class name
        SyntaxTree tree = CSharpSyntaxTree.ParseText("class C { C() { } }", cancellationToken: TestContext.Current.CancellationToken);
        ConstructorDeclarationSyntax ctor = tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes()
            .OfType<ConstructorDeclarationSyntax>()
            .First();

        // Act
        string? result = ctor.GetIdentifierText();

        // Assert
        result.ShouldBeNull();
    }

    [Fact]
    public void GetIdentifierText_DestructorDeclaration_ReturnsNull()
    {
        // Arrange — destructors have the special Roslyn name Finalize
        SyntaxTree tree = CSharpSyntaxTree.ParseText("class C { ~C() { } }", cancellationToken: TestContext.Current.CancellationToken);
        DestructorDeclarationSyntax dtor = tree.GetRoot(TestContext.Current.CancellationToken).DescendantNodes()
            .OfType<DestructorDeclarationSyntax>()
            .First();

        // Act
        string? result = dtor.GetIdentifierText();

        // Assert
        result.ShouldBeNull();
    }

    // ── ClassifyPosition: additional token kinds ────────────────────────────

    [Fact]
    public void ClassifyPosition_DisabledCode_ReturnsDisabledCode()
    {
        // Arrange — disabled code inside #if false
        const string source = """
                              #if false
                              class Hidden { }
                              #endif
                              class Visible { }
                              """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken);

        // Position 10 is inside the '#if false' block (inside "class Hidden { }")
        // The disabled text starts after "#if false\n" (10 chars including newline)
        int position = source.IndexOf("class Hidden", StringComparison.Ordinal) + 2;

        // Act
        PositionClassification result = RoslynExtensions.ClassifyPosition(tree, position);

        // Assert
        result.Kind.ShouldBe(PositionKind.DisabledCode);
    }

    [Fact]
    public void ClassifyPosition_CharacterLiteral_ReturnsStringLiteral()
    {
        // Arrange — 'A' character literal
        const string source = "class C { char c = 'A'; }";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken);

        // Position is inside the character literal 'A'
        int position = source.IndexOf("'A'", StringComparison.Ordinal) + 1;

        // Act
        PositionClassification result = RoslynExtensions.ClassifyPosition(tree, position);

        // Assert — char literal is classified as string literal
        result.Kind.ShouldBe(PositionKind.StringLiteral);
    }

    [Fact]
    public void ClassifyPosition_MultiLineComment_ReturnsComment()
    {
        // Arrange
        const string source = "/* block comment */\nclass C { }";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken);

        // Position inside the multi-line comment block
        int position = source.IndexOf("block", StringComparison.Ordinal);

        // Act
        PositionClassification result = RoslynExtensions.ClassifyPosition(tree, position);

        // Assert
        result.Kind.ShouldBe(PositionKind.Comment);
    }
}
