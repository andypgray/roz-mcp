using Zphil.Roz.Utility;

namespace Zphil.Roz.Tests.Utility;

public class FuzzyMatcherTests
{
    // ── LevenshteinDistance ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Circle", "Circle", 0, Label = "identical")]
    [InlineData("", "", 0, Label = "both empty")]
    [InlineData("Circl", "Circle", 1, Label = "single insertion")]
    [InlineData("Circle", "Circl", 1, Label = "single deletion")]
    [InlineData("Circle", "Corcle", 1, Label = "single substitution")]
    [InlineData("abc", "xyz", 3, Label = "completely different")]
    [InlineData("ab", "ba", 2, Label = "transposition")]
    [InlineData("", "abc", 3, Label = "empty vs non-empty")]
    [InlineData("abc", "", 3, Label = "non-empty vs empty")]
    [InlineData("circle", "CIRCLE", 0, Label = "case insensitive (lower vs upper)")]
    [InlineData("Shape", "shape", 0, Label = "case insensitive (title vs lower)")]
    public void LevenshteinDistance_ReturnsExpectedDistance(string a, string b, int expected) =>
        FuzzyMatcher.LevenshteinDistance(a, b).ShouldBe(expected);

    // ── GetSuggestions ──────────────────────────────────────────────────────

    [Fact]
    public void GetSuggestions_CloseMatch_ReturnsSorted()
    {
        List<string> candidates = ["Circle", "Rectangle", "Triangle"];

        List<string> suggestions = FuzzyMatcher.GetSuggestions("Circl", candidates);

        suggestions.ShouldContain("Circle");
        suggestions[0].ShouldBe("Circle");
    }

    [Fact]
    public void GetSuggestions_MultipleSimilar_ReturnsTopN()
    {
        List<string> candidates = ["Shape", "ShapeService", "ShapeHelper", "ShapeFactory", "ShapeBase", "IShape"];

        List<string> suggestions = FuzzyMatcher.GetSuggestions("Shap", candidates, 3);

        suggestions.Count.ShouldBeLessThanOrEqualTo(3);
        suggestions[0].ShouldBe("Shape"); // closest match (distance 1)
    }

    [Fact]
    public void GetSuggestions_NothingClose_ReturnsEmpty()
    {
        List<string> candidates = ["Circle", "Rectangle", "Triangle"];

        List<string> suggestions = FuzzyMatcher.GetSuggestions("Xyzzy123", candidates);

        suggestions.ShouldBeEmpty();
    }

    [Fact]
    public void GetSuggestions_ExactMatch_ExcludesSelf()
    {
        List<string> candidates = ["Circle", "Rectangle"];

        List<string> suggestions = FuzzyMatcher.GetSuggestions("Circle", candidates);

        suggestions.ShouldNotContain("Circle");
    }

    [Fact]
    public void GetSuggestions_EmptySearchTerm_ReturnsEmpty()
    {
        List<string> candidates = ["Circle", "Rectangle"];

        List<string> suggestions = FuzzyMatcher.GetSuggestions("", candidates);

        suggestions.ShouldBeEmpty();
    }

    [Fact]
    public void GetSuggestions_EmptyCandidates_ReturnsEmpty()
    {
        List<string> suggestions = FuzzyMatcher.GetSuggestions("Circle", []);

        suggestions.ShouldBeEmpty();
    }

    [Fact]
    public void GetSuggestions_RespectMaxCount()
    {
        List<string> candidates = ["Aa", "Ab", "Ac", "Ad", "Ae", "Af"];

        List<string> suggestions = FuzzyMatcher.GetSuggestions("Ax", candidates, 2);

        suggestions.Count.ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    public void GetSuggestions_CaseInsensitiveDedup()
    {
        List<string> candidates = ["Circle", "circle", "CIRCLE"];

        List<string> suggestions = FuzzyMatcher.GetSuggestions("Circl", candidates);

        // Should only have one entry despite case variations
        suggestions.Count.ShouldBe(1);
    }
}
