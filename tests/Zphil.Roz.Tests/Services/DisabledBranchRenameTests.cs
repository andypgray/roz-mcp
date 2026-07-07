using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Services;

namespace Zphil.Roz.Tests.Services;

public class DisabledBranchRenameTests
{
    [Fact]
    public void FindDisabledTextSpans_WithIfElse_FindsInactiveBranch()
    {
        // Arrange — NET9_0_OR_GREATER is defined, so #else branch is disabled
        var source = """
                     public class C
                     {
                     #if NET9_0_OR_GREATER
                         private readonly Lock lockObj = new();
                     #else
                         private readonly object lockObj = new();
                     #endif
                     }
                     """;

        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions()
            .WithPreprocessorSymbols("NET9_0_OR_GREATER"), cancellationToken: TestContext.Current.CancellationToken);

        // Act
        List<TextSpan> spans = RenameService.FindDisabledTextSpans(tree.GetRoot());

        // Assert
        spans.Count.ShouldBe(1);
        string disabledText = source.Substring(spans[0].Start, spans[0].Length);
        disabledText.ShouldContain("private readonly object lockObj");
    }

    [Fact]
    public void FindDisabledTextSpans_NoPreprocessorDirectives_ReturnsEmpty()
    {
        // Arrange
        var source = """
                     public class C
                     {
                         private int value;
                     }
                     """;

        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken);

        // Act
        List<TextSpan> spans = RenameService.FindDisabledTextSpans(tree.GetRoot());

        // Assert
        spans.ShouldBeEmpty();
    }

    [Fact]
    public void ReplaceInDisabledSpans_WholeWordOnly_DoesNotReplaceSubstring()
    {
        // Arrange — "Value" should not match "ValueTask"
        var source = "    private readonly ValueTask value = default;\n";
        List<TextSpan> spans = [new(0, source.Length)];

        // Act
        (string result, int count) = RenameService.ReplaceInDisabledSpans(source, spans, "Value", "Item");

        // Assert — "ValueTask" must remain untouched, but "value" (lowercase) also doesn't match "Value"
        result.ShouldContain("ValueTask");
        result.ShouldNotContain("ItemTask");
        count.ShouldBe(0);
    }

    [Fact]
    public void ReplaceInDisabledSpans_ExactMatch_Replaces()
    {
        // Arrange
        var source = "    private readonly object lockObj = new();\n";
        List<TextSpan> spans = [new(0, source.Length)];

        // Act
        (string result, int count) = RenameService.ReplaceInDisabledSpans(source, spans, "lockObj", "syncRoot");

        // Assert
        result.ShouldContain("syncRoot");
        result.ShouldNotContain("lockObj");
        count.ShouldBe(1);
    }

    [Fact]
    public void ReplaceInDisabledSpans_MultipleSpans_ReplacesAll()
    {
        // Arrange — two disabled regions, each containing the old name, with active code between
        var span0 = "    object lockObj = new();\n";
        var active = "    lock (lockObj) { }\n";
        var span1 = "    Lock lockObj = new();\n";
        string source = span0 + active + span1;

        List<TextSpan> spans =
        [
            new(0, span0.Length),
            new(span0.Length + active.Length, span1.Length)
        ];

        // Act
        (string result, int count) = RenameService.ReplaceInDisabledSpans(source, spans, "lockObj", "syncRoot");

        // Assert — both disabled spans replaced, active code untouched
        result.ShouldContain("object syncRoot");
        result.ShouldContain("Lock syncRoot");
        result.ShouldContain("lock (lockObj)");
        count.ShouldBe(2);
    }

    [Fact]
    public void ReplaceInDisabledSpans_NoDisabledSpans_ReturnsOriginal()
    {
        // Arrange
        var source = "private readonly object lockObj = new();";

        // Act
        (string result, int count) = RenameService.ReplaceInDisabledSpans(source, [], "lockObj", "syncRoot");

        // Assert
        result.ShouldBe(source);
        count.ShouldBe(0);
    }

    [Fact]
    public void ReplaceInDisabledSpans_NoMatchInSpan_ReturnsOriginal()
    {
        // Arrange — disabled span exists but doesn't contain the old name
        var source = "    private readonly object other = new();\n";
        List<TextSpan> spans = [new(0, source.Length)];

        // Act
        (string result, int count) = RenameService.ReplaceInDisabledSpans(source, spans, "lockObj", "syncRoot");

        // Assert
        result.ShouldBe(source);
        count.ShouldBe(0);
    }
}
