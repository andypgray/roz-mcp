using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Zphil.Roz.Models;

namespace Zphil.Roz.Symbols;

/// <summary>
///     Parses a user-supplied <c>newSignature</c> descriptor (a parameter-list string) into a
///     <see cref="ParsedSignature" />.
/// </summary>
/// <remarks>
///     Forgiving read-only input (<see cref="Normalize" />): a bare list without parens is wrapped, a
///     full method header is stripped down to its parameter list, and trailing garbage after the
///     matched close-paren is rejected — <see cref="SyntaxFactory.ParseParameterList(string,int,ParseOptions?,bool)" />
///     silently ignores trailing text, so <c>"(int a) bogus"</c> would half-parse without the guard.
///     Paren matching walks tokens (not raw characters) so a <c>)</c> inside a string/char default value
///     is not miscounted.
/// </remarks>
internal static class SignatureParser
{
    /// <summary>
    ///     Parses <paramref name="descriptor" /> into a <see cref="ParsedSignature" />, throwing
    ///     <see cref="UserErrorException" /> (echoing the raw text) on any parse failure.
    /// </summary>
    public static ParsedSignature Parse(string descriptor)
    {
        if (String.IsNullOrWhiteSpace(descriptor))
        {
            throw new UserErrorException("newSignature must be a parameter list, e.g. (string name, int count = 5).");
        }

        string normalized = Normalize(descriptor);

        ParameterListSyntax list = SyntaxFactory.ParseParameterList(normalized);
        Diagnostic? firstError = list.GetDiagnostics().FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
        if (firstError is not null)
        {
            throw new UserErrorException(
                $"Could not parse newSignature '{descriptor}': {firstError.GetMessage()} " +
                "Expected a parameter list, e.g. (string name, int count = 5).");
        }

        List<SignatureParameter> parameters = new(list.Parameters.Count);
        for (var i = 0; i < list.Parameters.Count; i++)
        {
            ParameterSyntax p = list.Parameters[i];
            if (p.Type is null || p.Identifier.IsMissing || p.Identifier.ValueText.Length == 0)
            {
                throw new UserErrorException(
                    $"Each parameter in newSignature '{descriptor}' needs a type and a name, e.g. (string name, int count = 5).");
            }

            parameters.Add(ToSignatureParameter(p, i));
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (SignatureParameter p in parameters)
        {
            if (!seen.Add(p.Name))
            {
                throw new UserErrorException(
                    $"newSignature '{descriptor}' declares parameter '{p.Name}' more than once.");
            }
        }

        return new ParsedSignature(list, parameters);
    }

    /// <summary>
    ///     Normalizes a raw descriptor to a parenthesized parameter list: wraps a bare list, strips a
    ///     leading method header down to its list, and rejects text after the matched close-paren.
    /// </summary>
    internal static string Normalize(string raw)
    {
        string trimmed = raw.Trim();
        int firstParen = trimmed.IndexOf('(');
        if (firstParen < 0)
        {
            return "(" + trimmed + ")";
        }

        // A bare list's first '(' can belong to a default value (`int a = Foo()`), which header-stripping
        // would silently reduce to `()` — an empty list, i.e. a remove-every-parameter delta. No method
        // header contains '=' before its parameter list, so such input is a bare list: wrap it whole.
        if (trimmed[..firstParen].Contains('='))
        {
            return "(" + trimmed + ")";
        }

        string stripped = trimmed[firstParen..];

        // Token-level paren matching: ParseTokens encapsulates string/char literals as single tokens,
        // so a ')' inside a default value (e.g. = ")") is never miscounted as a structural close.
        List<SyntaxToken> tokens = SyntaxFactory.ParseTokens(stripped).ToList();
        var depth = 0;
        SyntaxToken close = default;
        int closeIndex = -1;
        for (var i = 0; i < tokens.Count; i++)
        {
            SyntaxToken token = tokens[i];
            if (token.IsKind(SyntaxKind.OpenParenToken))
            {
                depth++;
            }
            else if (token.IsKind(SyntaxKind.CloseParenToken))
            {
                depth--;
                if (depth == 0)
                {
                    close = token;
                    closeIndex = i;
                    break;
                }
            }
        }

        if (closeIndex < 0)
        {
            throw new UserErrorException(
                $"newSignature '{raw}' has an unbalanced parameter list — expected a matching ')'.");
        }

        for (int i = closeIndex + 1; i < tokens.Count; i++)
        {
            if (!tokens[i].IsKind(SyntaxKind.EndOfFileToken))
            {
                throw new UserErrorException(
                    $"newSignature '{raw}' has unexpected text after the parameter list's ')'. " +
                    "Pass only the parameter list, e.g. (string name, int count = 5).");
            }
        }

        return stripped[..close.Span.End];
    }

    private static SignatureParameter ToSignatureParameter(ParameterSyntax p, int ordinal)
    {
        RefKind refKind = RefKind.None;
        var isParams = false;
        foreach (SyntaxToken modifier in p.Modifiers)
        {
            switch (modifier.Kind())
            {
                case SyntaxKind.OutKeyword:
                    refKind = RefKind.Out;
                    break;
                case SyntaxKind.RefKeyword:
                    refKind = RefKind.Ref;
                    break;
                case SyntaxKind.InKeyword:
                    refKind = RefKind.In;
                    break;
                case SyntaxKind.ParamsKeyword:
                    isParams = true;
                    break;
            }
        }

        return new SignatureParameter(
            p.Identifier.ValueText,
            p.Type!,
            p.Type!.ToString(),
            refKind,
            isParams,
            p.Default is not null,
            p.Default?.Value.ToString(),
            ordinal);
    }
}
