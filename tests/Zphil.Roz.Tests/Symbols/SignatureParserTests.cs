using Microsoft.CodeAnalysis;
using Zphil.Roz.Models;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Tests.Symbols;

/// <summary>
///     Unit tests for <see cref="SignatureParser" /> — the forgiving <c>newSignature</c> normalization
///     and parse. Pure string/syntax logic, no workspace.
/// </summary>
public class SignatureParserTests
{
    [Fact]
    public void Normalize_BareList_Wraps() => SignatureParser.Normalize("string name, int count = 5").ShouldBe("(string name, int count = 5)");

    [Fact]
    public void Normalize_CanonicalForm_Unchanged() => SignatureParser.Normalize("(int a)").ShouldBe("(int a)");

    [Fact]
    public void Normalize_FullHeader_StripsPrefixAndTrailing() => SignatureParser.Normalize("public void Foo(int a, string b)").ShouldBe("(int a, string b)");

    [Fact]
    public void Normalize_DefaultWithParens_MatchesOuterClose()
    {
        // The ')' inside default(int) must not be miscounted as the parameter list's close.
        SignatureParser.Normalize("(int a = default(int))").ShouldBe("(int a = default(int))");
    }

    [Fact]
    public void Normalize_TrailingGarbage_Throws()
    {
        // ParseParameterList silently ignores trailing text — the normalizer must reject it.
        Should.Throw<UserErrorException>(() => SignatureParser.Normalize("(int a) bogus"))
            .Message.ShouldContain("unexpected text");
    }

    [Fact]
    public void Parse_Valid_ExtractsParameters()
    {
        ParsedSignature parsed = SignatureParser.Parse("(string name, int count = 5)");

        parsed.Parameters.Count.ShouldBe(2);
        parsed.Parameters[0].Name.ShouldBe("name");
        parsed.Parameters[0].HasExplicitDefault.ShouldBeFalse();
        parsed.Parameters[1].Name.ShouldBe("count");
        parsed.Parameters[1].HasExplicitDefault.ShouldBeTrue();
        parsed.Parameters[1].DefaultText.ShouldBe("5");
    }

    [Fact]
    public void Parse_RefKindAndParams_Detected()
    {
        ParsedSignature parsed = SignatureParser.Parse("(ref int a, params int[] xs)");

        parsed.Parameters[0].RefKind.ShouldBe(RefKind.Ref);
        parsed.Parameters[1].IsParams.ShouldBeTrue();
    }

    [Fact]
    public void Parse_Invalid_Throws() => Should.Throw<UserErrorException>(() => SignatureParser.Parse("(123 456)"));

    [Fact]
    public void Parse_DuplicateName_Throws()
    {
        Should.Throw<UserErrorException>(() => SignatureParser.Parse("(int a, string a)"))
            .Message.ShouldContain("more than once");
    }

    [Fact]
    public void Parse_Empty_Throws() => Should.Throw<UserErrorException>(() => SignatureParser.Parse("   "));

    [Fact]
    public void Normalize_BareListWithParenthesizedDefault_Wraps()
    {
        // The first '(' belongs to the default value — header-stripping would silently reduce this to
        // "()" (an empty list, i.e. remove every parameter). '=' before the first '(' marks a bare list.
        SignatureParser.Normalize("int a = Foo()").ShouldBe("(int a = Foo())");
    }

    [Fact]
    public void Parse_BareListWithParenthesizedDefault_ParsesParameter()
    {
        ParsedSignature parsed = SignatureParser.Parse("int a = Foo()");

        parsed.Parameters.Count.ShouldBe(1);
        parsed.Parameters[0].Name.ShouldBe("a");
        parsed.Parameters[0].HasExplicitDefault.ShouldBeTrue();
    }
}
