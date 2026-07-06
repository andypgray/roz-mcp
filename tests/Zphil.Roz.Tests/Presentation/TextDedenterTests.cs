using Zphil.Roz.Presentation;

namespace Zphil.Roz.Tests.Presentation;

public class TextDedenterTests
{
    // ── Dedent ───────────────────────────────────────────────────────────

    [Fact]
    public void Dedent_EmptyArray_ReturnsEmptyArray()
    {
        string[] result = TextDedenter.Dedent(Array.Empty<string>());

        result.ShouldBeEmpty();
    }

    [Fact]
    public void Dedent_SingleLine_TrimsLeadingWhitespace()
    {
        string[] result = TextDedenter.Dedent(["        int x = 1;"]);

        result.ShouldBe(["int x = 1;"]);
    }

    [Fact]
    public void Dedent_MultiLineWithCommonIndent_StripsCommonPrefix()
    {
        string[] input =
        [
            "        public void Foo()",
            "        {",
            "            int x = 1;",
            "        }"
        ];

        string[] result = TextDedenter.Dedent(input);

        result.ShouldBe([
            "public void Foo()",
            "{",
            "    int x = 1;",
            "}"
        ]);
    }

    [Fact]
    public void Dedent_WhitespaceOnlyLinesIgnoredInMinCalculation()
    {
        string[] input =
        [
            "        public void Foo()",
            "",
            "   ",
            "        {",
            "            return;",
            "        }"
        ];

        string[] result = TextDedenter.Dedent(input);

        result.ShouldBe([
            "public void Foo()",
            "",
            "   ",
            "{",
            "    return;",
            "}"
        ]);
    }

    [Fact]
    public void Dedent_NoCommonIndent_ReturnsUnchanged()
    {
        string[] input =
        [
            "public class Foo",
            "{",
            "    int x;",
            "}"
        ];

        string[] result = TextDedenter.Dedent(input);

        result.ShouldBe(input);
    }

    [Fact]
    public void Dedent_MixedIndentDepths_StripsOnlyMinimum()
    {
        string[] input =
        [
            "    if (true)",
            "    {",
            "        return 1;",
            "    }"
        ];

        string[] result = TextDedenter.Dedent(input);

        result.ShouldBe([
            "if (true)",
            "{",
            "    return 1;",
            "}"
        ]);
    }

    [Fact]
    public void Dedent_AllWhitespaceOnlyLines_ReturnsUnchanged()
    {
        string[] input = ["   ", "  ", ""];

        string[] result = TextDedenter.Dedent(input);

        result.ShouldBe(input);
    }

    [Fact]
    public void Dedent_LinesWithCarriageReturns_PreservesThem()
    {
        string[] input = ["        int x;\r", "        int y;\r"];

        string[] result = TextDedenter.Dedent(input);

        result.ShouldBe(["int x;\r", "int y;\r"]);
    }
}
