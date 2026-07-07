using Zphil.Roz.Symbols;

namespace Zphil.Roz.Tests.Symbols;

public class GenericArityParserTests
{
    [Theory]
    [InlineData("Grain<>", "Grain", 1)]
    [InlineData("Grain<,>", "Grain", 2)]
    [InlineData("Grain<,,>", "Grain", 3)]
    [InlineData("Processor<>", "Processor", 1)]
    [InlineData("A<,,,>", "A", 4)]
    public void TryParseArity_ValidOpenGeneric_ReturnsTrue(string input, string expectedBase, int expectedArity)
    {
        // Act
        bool result = GenericArityParser.TryParseArity(input, out string baseName, out int arity);

        // Assert
        result.ShouldBeTrue();
        baseName.ShouldBe(expectedBase);
        arity.ShouldBe(expectedArity);
    }

    [Theory]
    [InlineData("IRepository`1", "IRepository", 1)]
    [InlineData("IDictionary`2", "IDictionary", 2)]
    [InlineData("Foo`10", "Foo", 10)]
    [InlineData("MyNamespace.IRepository`1", "MyNamespace.IRepository", 1)]
    public void TryParseArity_BacktickArity_ReturnsTrue(string input, string expectedBase, int expectedArity)
    {
        // Act — CLR metadata notation is accepted alongside angle-bracket open-generic syntax.
        bool result = GenericArityParser.TryParseArity(input, out string baseName, out int arity);

        // Assert
        result.ShouldBeTrue();
        baseName.ShouldBe(expectedBase);
        arity.ShouldBe(expectedArity);
    }

    [Theory]
    [InlineData("Grain")]
    [InlineData("Grain<string>")]
    [InlineData("Grain<T>")]
    [InlineData("Grain<T, U>")]
    [InlineData("Dictionary<string, int>")]
    [InlineData("Foo`0")] // arity 0 is non-generic; spurious backtick
    [InlineData("Foo`")] // no digits after backtick
    [InlineData("Foo`x")] // non-digit suffix
    [InlineData("Foo`99999999999999999999")] // arity overflows Int32 — TryParse fails, no crash
    [InlineData("Foo`1<>")] // mixed notation — ambiguous intent
    [InlineData("<>")]
    [InlineData("")]
    [InlineData("Grain<string,>")]
    [InlineData("Grain<,string>")]
    public void TryParseArity_InvalidOrNonGeneric_ReturnsFalse(string input)
    {
        // Act
        bool result = GenericArityParser.TryParseArity(input, out _, out _);

        // Assert
        result.ShouldBeFalse();
    }

    [Theory]
    [InlineData("Grain", null, "Grain")]
    [InlineData("Grain", 1, "Grain<>")]
    [InlineData("Grain", 2, "Grain<,>")]
    [InlineData("Grain", 3, "Grain<,,>")]
    public void FormatWithArity_ReconstructsDisplayName(string baseName, int? arity, string expected)
    {
        // Act
        string result = GenericArityParser.FormatWithArity(baseName, arity);

        // Assert
        result.ShouldBe(expected);
    }
}
