namespace Zphil.Roz.Symbols;

/// <summary>
///     Parses fully-qualified symbol names (e.g. "MyNamespace.MyClass.MyMethod") into
///     candidate (typeFqn, memberName) pairs for resolution against Roslyn compilations.
/// </summary>
internal static class FqnParser
{
    /// <summary>
    ///     Returns <c>true</c> if <paramref name="symbolName" /> looks like a fully-qualified name
    ///     (contains dots and is not a bare special member like <c>.ctor</c>).
    /// </summary>
    internal static bool IsFqn(string symbolName) =>
        symbolName.Contains('.') && !SpecialMemberResolver.IsSpecialMemberName(symbolName);

    /// <summary>
    ///     Returns <c>true</c> if <paramref name="symbolName" /> contains generic syntax
    ///     (angle brackets or backtick arity notation) that we don't support in FQN resolution.
    /// </summary>
    internal static bool ContainsGenericSyntax(string symbolName) =>
        symbolName.Contains('<') || symbolName.Contains('`');

    /// <summary>Simple (unqualified) name: the segment after the last dot, or the whole string when undotted.</summary>
    internal static string SimpleName(string name)
    {
        int lastDot = name.LastIndexOf('.');
        return lastDot >= 0 ? name[(lastDot + 1)..] : name;
    }

    /// <summary>Namespace/qualifier before the last dot, or "" when undotted.</summary>
    internal static string Namespace(string name)
    {
        int lastDot = name.LastIndexOf('.');
        return lastDot >= 0 ? name[..lastDot] : "";
    }

    /// <summary>
    ///     Validates <paramref name="symbolName" /> for FQN-related constraints and throws
    ///     <see cref="UserErrorException" /> if generic syntax is detected or if a dotted name
    ///     is combined with <paramref name="containingType" />.
    /// </summary>
    internal static void ThrowIfInvalid(string symbolName, string? containingType)
    {
        if (ContainsGenericSyntax(symbolName))
        {
            // Open generic syntax (e.g. "Processor<>", "Processor<,>") is allowed for arity disambiguation —
            // callers strip it before passing to ThrowIfInvalid. If we still see generic syntax here,
            // it's concrete generics (e.g. "Processor<string>") which are not supported.
            throw new UserErrorException(
                $"Generic FQN syntax ('{symbolName}') is not supported. " +
                "Use the simple name (e.g. 'ShapeProcessor') with containingType to disambiguate, " +
                "or use open generic syntax (e.g. 'ShapeProcessor<>') for arity disambiguation, " +
                "or drop symbolName and use locations=['path:line:col'].");
        }

        if (IsFqn(symbolName) && containingType is not null)
        {
            throw new UserErrorException(
                $"symbolName '{symbolName}' appears to be a fully-qualified name (contains dots). " +
                "Do not combine with containingType — either pass the FQN as symbolName alone, " +
                "or use simple symbolName + containingType.");
        }
    }

    /// <summary>
    ///     Decomposes a dotted FQN into ordered candidate interpretations.
    ///     Each candidate is a (typeFqn, memberName) pair where memberName is <c>null</c>
    ///     when the entire string is treated as a type FQN.
    /// </summary>
    /// <remarks>
    ///     For <c>A.B.C</c>: candidate 1 = (typeFqn="A.B", member="C"),
    ///     candidate 2 = (typeFqn="A.B.C", member=null).
    ///     For <c>Circle..ctor</c>: the double-dot is handled by detecting the special member
    ///     suffix and splitting before it.
    /// </remarks>
    internal static IReadOnlyList<FqnCandidate> Decompose(string fqn)
    {
        // Handle special member suffixes like ".ctor", ".cctor" which create double-dots
        (string prefix, string? specialMember) = ExtractSpecialMemberSuffix(fqn);

        if (specialMember is not null)
        {
            // "Circle..ctor" → prefix="Circle", specialMember=".ctor"
            // "TestFixture.Shapes.Circle..ctor" → prefix="TestFixture.Shapes.Circle", specialMember=".ctor"
            return
            [
                new FqnCandidate(prefix, specialMember)
                // No type-only candidate — a trailing .ctor/.cctor always means member
            ];
        }

        int lastDot = fqn.LastIndexOf('.');
        if (lastDot <= 0)
        {
            // No dot or dot at start — shouldn't happen if IsFqn was checked, but handle gracefully
            return [new FqnCandidate(fqn, null)];
        }

        string typePart = fqn[..lastDot];
        string memberPart = fqn[(lastDot + 1)..];

        return
        [
            new FqnCandidate(typePart, memberPart),
            new FqnCandidate(fqn, null)
        ];
    }

    /// <summary>
    ///     Detects special member suffixes (.ctor, .cctor) that follow a double-dot pattern
    ///     and splits them from the type prefix.
    /// </summary>
    private static (string Prefix, string? SpecialMember) ExtractSpecialMemberSuffix(string fqn)
    {
        // Check for "..cctor" before "..ctor" since "..ctor" is a substring of "..cctor"
        int doubleDotCctor = fqn.IndexOf("..cctor", StringComparison.Ordinal);
        if (doubleDotCctor >= 0)
        {
            return (fqn[..doubleDotCctor], ".cctor");
        }

        int doubleDotCtor = fqn.IndexOf("..ctor", StringComparison.Ordinal);
        if (doubleDotCtor >= 0)
        {
            return (fqn[..doubleDotCtor], ".ctor");
        }

        return (fqn, null);
    }
}

/// <summary>
///     A candidate interpretation of a fully-qualified name: a type FQN plus an optional member name.
/// </summary>
internal readonly record struct FqnCandidate(string TypeFqn, string? MemberName);
