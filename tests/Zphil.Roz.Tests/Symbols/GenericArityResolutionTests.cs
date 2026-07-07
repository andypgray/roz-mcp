using System.Text.RegularExpressions;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Symbols;

public class GenericArityResolutionTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools navigationTools = CreateNavigationTools(fixture);
    private readonly ReferenceTools referenceTools = CreateReferenceTools(fixture);

    // ── find_symbol with arity syntax ───────────────────────────────────────

    [Theory]
    [InlineData("Processor<>", "Processor<T>", "Processor<TInput, TOutput>")]
    [InlineData("Processor<,>", "Processor<TInput, TOutput>", "Processor<T>")]
    public async Task FindSymbol_OpenGenericArity_ReturnsOnlyMatchingArity(
        string searchTerm, string expected, string excluded)
    {
        string result = await navigationTools.FindSymbol([searchTerm], ct: TestContext.Current.CancellationToken);

        result.ShouldContain(expected);
        result.ShouldNotContain(excluded);
    }

    [Fact]
    public async Task FindSymbol_BareNameNoArity_ReturnsAll()
    {
        // Act — bare "Processor" without arity should return all variants
        string result = await navigationTools.FindSymbol(["Processor"], ct: TestContext.Current.CancellationToken);

        // Assert — all three arities present. The non-generic (arity 0) is asserted distinctly via
        // its "public class Processor" declaration with no type-parameter list; a bare
        // ShouldContain("Processor") is subsumed by the generic variants and BasicProcessor.
        Regex.IsMatch(result, @"public class Processor(?!<)").ShouldBeTrue("non-generic Processor (arity 0) should be listed");
        result.ShouldContain("Processor<T>");
        result.ShouldContain("Processor<TInput, TOutput>");
    }

    // ── echo line reflects arity ─────────────────────────────────────────────────

    [Theory]
    [InlineData("Processor<>")]
    [InlineData("Processor<,>")]
    [InlineData("Processor")]
    public async Task FindSymbol_EchoLine_QuotesSearchTermVerbatim(string searchTerm)
    {
        string result = await navigationTools.FindSymbol([searchTerm], ct: TestContext.Current.CancellationToken);

        result.ShouldContain($"Found symbol(s) matching \"{searchTerm}\"");
    }

    // ── find_implementations on types with arity syntax ─────────────────────────

    [Theory]
    [InlineData("Processor<>", "StringProcessor", "PairProcessor", "BasicProcessor")]
    [InlineData("Processor<,>", "PairProcessor", "StringProcessor", "BasicProcessor")]
    public async Task FindImplementations_OnType_OpenGenericArity_ReturnsMatchingAritySubtypes(
        string searchTerm, string expectedSubtype, string excludedSubtype1, string excludedSubtype2)
    {
        string result = await referenceTools.FindImplementations(symbolNames: [searchTerm], ct: TestContext.Current.CancellationToken);

        result.ShouldContain(expectedSubtype);
        result.ShouldNotContain(excludedSubtype1);
        result.ShouldNotContain(excludedSubtype2);
    }

    [Fact]
    public async Task FindImplementations_OnType_FqnOpenGenericArity_ResolvesUniqueArityNoAmbiguity()
    {
        // Act — dotted FQN + open-generic arity goes through SymbolResolver's IsFqnMatched branch (CR-4).
        // The simple-name "Processor<>" case above takes the non-FQN path, so it can't catch this bug.
        string result = await referenceTools.FindImplementations(
            symbolNames: ["TestFixture.Services.Processor<>"], ct: TestContext.Current.CancellationToken);

        // Assert — resolves to the arity-1 type only, no spurious ambiguity
        result.ShouldNotContain("Ambiguous"); // ThrowAmbiguityError emits "Ambiguous: N symbols match …"
        result.ShouldContain("StringProcessor"); // : Processor<string>
        result.ShouldNotContain("PairProcessor"); // : Processor<string, int> (arity 2)
        result.ShouldNotContain("BasicProcessor"); // : Processor (arity 0)
    }

    // ── Bare name auto-resolves to non-generic ────────────────────────────────

    [Fact]
    public async Task FindImplementations_OnType_BareProcessorName_ResolvesNonGeneric()
    {
        // Act — bare "Processor" auto-resolves to non-generic Processor
        string result = await referenceTools.FindImplementations(symbolNames: ["Processor"], ct: TestContext.Current.CancellationToken);

        // Assert — BasicProcessor extends non-generic Processor
        result.ShouldContain("BasicProcessor");
        result.ShouldNotContain("StringProcessor");
        result.ShouldNotContain("PairProcessor");
    }

    [Fact]
    public async Task FindImplementations_OnType_BareNameNoArity0Exists_ReturnsAmbiguity()
    {
        // Act — "Widget" has no non-generic variant, only Widget<T> and Widget<T1,T2>
        string result = await referenceTools.FindImplementations(symbolNames: ["Widget"], ct: TestContext.Current.CancellationToken);

        // Assert — auto-resolution requires arity 0; ambiguity error is captured inline
        result.ShouldContain("Ambiguous");
        result.ShouldContain("Widget<>");
        result.ShouldContain("Widget<,>");
    }

    // ── Position-based resolves non-generic Processor ────────────────────────

    [Fact]
    public async Task FindImplementations_OnType_PositionBased_ResolvesNonGenericProcessor()
    {
        // Arrange — non-generic Processor is at line 6 of GenericArity.cs
        string filePath = fixture.ServicesFile("GenericArity.cs");

        // Act
        string result = await referenceTools.FindImplementations([Loc(filePath, 6, 14)], ct: TestContext.Current.CancellationToken);

        // Assert — BasicProcessor extends non-generic Processor
        result.ShouldContain("BasicProcessor");
    }

    // ── Backtick arity notation (CLR metadata style) ─────────────────────────

    [Fact]
    public async Task FindSymbol_BacktickArity1_ReturnsOnlyArity1()
    {
        // Act — backtick notation is accepted alongside angle-bracket syntax
        string result = await navigationTools.FindSymbol(["Processor`1"], ct: TestContext.Current.CancellationToken);

        // Assert — matches same behavior as "Processor<>"
        result.ShouldContain("Processor<T>");
        result.ShouldNotContain("Processor<TInput, TOutput>");
    }

    [Fact]
    public async Task FindImplementations_OnType_BacktickArity2_ReturnsArity2Subtypes()
    {
        // Act — matches same behavior as "Widget<,>"
        string result = await referenceTools.FindImplementations(symbolNames: ["Widget`2"], ct: TestContext.Current.CancellationToken);

        // Assert — PairWidget extends Widget<string, int>
        result.ShouldContain("PairWidget");
        result.ShouldNotContain("StringWidget");
    }
}
