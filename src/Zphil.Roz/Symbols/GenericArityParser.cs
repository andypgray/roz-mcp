using Microsoft.CodeAnalysis;

namespace Zphil.Roz.Symbols;

/// <summary>
///     Parses open generic syntax (e.g. <c>Processor&lt;&gt;</c>, <c>Processor&lt;,&gt;</c>) from symbol names
///     to extract the base name and generic arity for disambiguation.
/// </summary>
internal static class GenericArityParser
{
    /// <summary>
    ///     Attempts to parse open generic arity syntax from a symbol name.
    ///     Recognizes both C#-style open generics (<c>Name&lt;&gt;</c>, <c>Name&lt;,&gt;</c>)
    ///     and CLR metadata backtick notation (<c>Name`1</c>, <c>Name`2</c>).
    /// </summary>
    /// <param name="symbolName">The symbol name, possibly containing open generic syntax.</param>
    /// <param name="baseName">The name without generic syntax (e.g. "Processor" from "Processor&lt;&gt;" or "Processor`1").</param>
    /// <param name="arity">The number of type parameters.</param>
    /// <returns>
    ///     <c>true</c> if <paramref name="symbolName" /> uses valid open generic syntax
    ///     (angle brackets or backtick+digits); <c>false</c> for plain names or concrete
    ///     generics (<c>Processor&lt;string&gt;</c>).
    /// </returns>
    internal static bool TryParseArity(string symbolName, out string baseName, out int arity)
    {
        if (TryParseBacktickArity(symbolName, out baseName, out arity))
        {
            return true;
        }

        // A stray backtick that didn't parse as valid arity is mixed/malformed notation
        // (e.g. "Foo`x", "Foo`1<>") — don't fall through to the angle-bracket path so
        // downstream FqnParser.ThrowIfInvalid rejects it with a clear message.
        if (symbolName.Contains('`'))
        {
            return false;
        }

        if (!symbolName.EndsWith('>'))
        {
            return false;
        }

        int openBracket = symbolName.LastIndexOf('<');
        if (openBracket <= 0)
        {
            return false;
        }

        // Content between < and > must be empty or all commas
        ReadOnlySpan<char> content = symbolName.AsSpan(openBracket + 1, symbolName.Length - openBracket - 2);
        foreach (char c in content)
        {
            if (c != ',')
            {
                return false;
            }
        }

        baseName = symbolName[..openBracket];
        arity = content.Length + 1;
        return true;
    }

    /// <summary>
    ///     Recognizes CLR metadata backtick arity (<c>Foo`1</c>, <c>Foo`10</c>). Requires
    ///     at least one trailing digit after the backtick; returns <c>false</c> for bare
    ///     <c>Foo`</c>, <c>Foo`0</c> (non-generic), or mixed forms like <c>Foo`1&lt;&gt;</c>.
    /// </summary>
    private static bool TryParseBacktickArity(string symbolName, out string baseName, out int arity)
    {
        baseName = symbolName;
        arity = 0;

        int backtick = symbolName.LastIndexOf('`');
        if (backtick <= 0 || backtick == symbolName.Length - 1)
        {
            return false;
        }

        ReadOnlySpan<char> digits = symbolName.AsSpan(backtick + 1);
        foreach (char c in digits)
        {
            if (!Char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        // TryParse (not Parse) so an arity that overflows Int32 (e.g. "Foo`99999999999999999999")
        // is treated as "not arity" rather than throwing — FqnParser.ThrowIfInvalid then emits the
        // friendly generic-syntax error downstream.
        if (!Int32.TryParse(digits, out int parsed))
        {
            return false;
        }

        if (parsed == 0)
        {
            return false;
        }

        baseName = symbolName[..backtick];
        arity = parsed;
        return true;
    }

    /// <summary>
    ///     Extracts the base name and optional arity from a symbol name that may use open generic syntax.
    /// </summary>
    internal static (string SearchName, int? Arity) ExtractArity(string symbolName) =>
        TryParseArity(symbolName, out string baseName, out int arity)
            ? (baseName, arity)
            : (symbolName, null);

    /// <summary>
    ///     Returns the generic type parameter count for a symbol.
    /// </summary>
    internal static int GetGenericArity(ISymbol symbol) => symbol switch
    {
        INamedTypeSymbol nts => nts.TypeParameters.Length,
        IMethodSymbol ms => ms.TypeParameters.Length,
        _ => 0
    };

    /// <summary>
    ///     Reconstructs the open generic display name from a base name and arity.
    ///     E.g. ("Processor", 1) → "Processor&lt;&gt;", ("Processor", 2) → "Processor&lt;,&gt;".
    ///     Returns the base name unchanged when arity is null.
    /// </summary>
    internal static string FormatWithArity(string baseName, int? arity) =>
        arity is null ? baseName : $"{baseName}<{new string(',', arity.Value - 1)}>";

    /// <summary>
    ///     Filters symbols to only those matching the specified generic arity.
    /// </summary>
    internal static List<ISymbol> FilterByArity(List<ISymbol> symbols, int arity) =>
        symbols.Where(s => GetGenericArity(s) == arity).ToList();
}
