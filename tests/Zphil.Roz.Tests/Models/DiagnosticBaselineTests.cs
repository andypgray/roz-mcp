using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Models;
using DiagnosticSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Zphil.Roz.Tests.Models;

public class DiagnosticBaselineTests
{
    private const string DummySource = "class C { }";

    [Fact]
    public void DiagnosticKey_SameValues_AreEqual()
    {
        var key1 = new DiagnosticKey("CS0168", "Shapes/Circle.cs", "The variable 'x' is declared but never used");
        var key2 = new DiagnosticKey("CS0168", "Shapes/Circle.cs", "The variable 'x' is declared but never used");

        key1.ShouldBe(key2);
        key1.GetHashCode().ShouldBe(key2.GetHashCode());
    }

    [Theory]
    [InlineData("CS0168", "Shapes/Circle.cs", "msg", "CS0169", "Shapes/Circle.cs", "msg")]
    [InlineData("CS0168", "Shapes/Circle.cs", "msg", "CS0168", "Shapes/Rectangle.cs", "msg")]
    [InlineData("CS0168", "Shapes/Circle.cs", "msg1", "CS0168", "Shapes/Circle.cs", "msg2")]
    public void DiagnosticKey_AnyFieldDiffers_AreNotEqual(
        string id1, string file1, string msg1,
        string id2, string file2, string msg2)
    {
        var key1 = new DiagnosticKey(id1, file1, msg1);
        var key2 = new DiagnosticKey(id2, file2, msg2);

        key1.ShouldNotBe(key2);
    }

    [Fact]
    public void DiagnosticKey_WorksInHashSet()
    {
        var key1 = new DiagnosticKey("CS0168", "Shapes/Circle.cs", "msg");
        var key2 = new DiagnosticKey("CS0168", "Shapes/Circle.cs", "msg");
        var key3 = new DiagnosticKey("CS0169", "Shapes/Circle.cs", "msg");

        HashSet<DiagnosticKey> set = new() { key1, key2, key3 };

        set.Count.ShouldBe(2);
        set.ShouldContain(key1);
        set.ShouldContain(key3);
    }

    // ── CaptureFrom filtering ────────────────────────────────────────────────

    [Fact]
    public void CaptureFrom_HiddenSeverity_IsExcluded()
    {
        // Arrange
        List<Diagnostic> diagnostics =
        [
            CreateSourceDiagnostic("CS0168", DiagnosticSeverity.Warning, "unused variable", "Shapes/Circle.cs"),
            CreateSourceDiagnostic("IDE0051", DiagnosticSeverity.Hidden, "private member unused", "Shapes/Circle.cs")
        ];

        // Act
        var baseline = DiagnosticBaseline.CaptureFrom(diagnostics, @"C:\solution");

        // Assert
        baseline.Count.ShouldBe(1);
        baseline.Keys.ShouldContain(k => k.Id == "CS0168");
        baseline.Keys.ShouldNotContain(k => k.Id == "IDE0051");
    }

    [Fact]
    public void CaptureFrom_GeneratedFile_IsExcluded()
    {
        // Arrange
        List<Diagnostic> diagnostics =
        [
            CreateSourceDiagnostic("CS0168", DiagnosticSeverity.Warning, "unused variable", "Shapes/Circle.cs"),
            CreateSourceDiagnostic("CS0169", DiagnosticSeverity.Warning, "unused field", "obj/Generated.g.cs")
        ];

        // Act
        var baseline = DiagnosticBaseline.CaptureFrom(diagnostics, @"C:\solution");

        // Assert
        baseline.Count.ShouldBe(1);
        baseline.Keys.ShouldContain(k => k.Id == "CS0168");
        baseline.Keys.ShouldNotContain(k => k.Id == "CS0169");
    }

    [Fact]
    public void CaptureFrom_MetadataLocation_IsExcluded()
    {
        // Arrange
        var descriptor = new DiagnosticDescriptor("CS0001", "title", "error", "category", DiagnosticSeverity.Error, true);
        var metadataDiag = Diagnostic.Create(descriptor, Location.None);
        List<Diagnostic> diagnostics = [metadataDiag];

        // Act
        var baseline = DiagnosticBaseline.CaptureFrom(diagnostics, @"C:\solution");

        // Assert
        baseline.Count.ShouldBe(0);
    }

    [Fact]
    public void CaptureFrom_MixedDiagnostics_OnlyKeepsSourceWarningsAndErrors()
    {
        // Arrange
        List<Diagnostic> diagnostics =
        [
            CreateSourceDiagnostic("CS0168", DiagnosticSeverity.Warning, "warning in source", "Shapes/Circle.cs"),
            CreateSourceDiagnostic("CS0246", DiagnosticSeverity.Error, "error in source", "Shapes/Shape.cs"),
            CreateSourceDiagnostic("IDE0001", DiagnosticSeverity.Hidden, "hidden diag", "Shapes/Circle.cs"),
            CreateSourceDiagnostic("CS0169", DiagnosticSeverity.Warning, "warning in generated", "obj/Debug/Generated.g.cs")
        ];

        // Act
        var baseline = DiagnosticBaseline.CaptureFrom(diagnostics, @"C:\solution");

        // Assert
        baseline.Count.ShouldBe(2);
        baseline.Keys.ShouldContain(k => k.Id == "CS0168");
        baseline.Keys.ShouldContain(k => k.Id == "CS0246");
    }

    [Fact]
    public void CaptureFrom_EmptyList_ReturnsEmptyBaseline()
    {
        // Act
        var baseline = DiagnosticBaseline.CaptureFrom([], @"C:\solution");

        // Assert
        baseline.Count.ShouldBe(0);
    }

    // ── Severity scoping ─────────────────────────────────────────────────────

    [Fact]
    public void KeysAtOrAboveSeverity_ExcludesKeysBelowFloor()
    {
        // Arrange — a Warning and an Error captured together.
        List<Diagnostic> diagnostics =
        [
            CreateSourceDiagnostic("CS0219", DiagnosticSeverity.Warning, "unused local", "Shapes/Circle.cs"),
            CreateSourceDiagnostic("CS0246", DiagnosticSeverity.Error, "missing type", "Shapes/Shape.cs")
        ];
        var baseline = DiagnosticBaseline.CaptureFrom(diagnostics, @"C:\solution");

        // Act — query at the Error floor.
        IReadOnlyCollection<DiagnosticKey> atError = baseline.KeysAtOrAboveSeverity(DiagnosticSeverity.Error);

        // Assert — the below-floor Warning is dropped; the Error survives.
        atError.Count.ShouldBe(1);
        atError.ShouldContain(k => k.Id == "CS0246");
        atError.ShouldNotContain(k => k.Id == "CS0219");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static Diagnostic CreateSourceDiagnostic(string id, DiagnosticSeverity severity, string message, string filePath)
    {
        var descriptor = new DiagnosticDescriptor(id, "title", message, "category", severity, true);
        SyntaxTree tree = CSharpSyntaxTree.ParseText(DummySource, path: filePath);
        SourceText text = tree.GetText();
        int position = text.Lines[0].Start;
        var location = Location.Create(tree, new TextSpan(position, 1));
        return Diagnostic.Create(descriptor, location);
    }
}
