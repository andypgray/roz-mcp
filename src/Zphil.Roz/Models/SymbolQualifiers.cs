using Microsoft.CodeAnalysis;

namespace Zphil.Roz.Models;

/// <summary>
///     Precomputed containing-type qualifications for a resolved symbol, used to
///     disambiguate batch section headers only when bare names collide. <see cref="Short" />
///     is minimally-qualified (<c>Type.Member</c>); <see cref="Full" /> is namespace-qualified
///     (<c>Namespace.Type.Member</c>), the fallback when two batched symbols share both a
///     simple name and a simple containing-type name across different namespaces. The display
///     format is applied to the containing type (no return type/parameters) and the member's
///     bare name is appended.
/// </summary>
internal readonly record struct SymbolQualifiers(string Short, string Full)
{
    internal static readonly SymbolDisplayFormat FullyQualifiedWithoutGlobal =
        SymbolDisplayFormat.FullyQualifiedFormat
            .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

    /// <summary>Both qualifications equal to the bare name — used when no symbol is available.</summary>
    public static SymbolQualifiers Bare(string name) => new(name, name);

    public static SymbolQualifiers For(ISymbol symbol)
    {
        if (symbol is INamedTypeSymbol)
        {
            return new SymbolQualifiers(
                symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                symbol.ToDisplayString(FullyQualifiedWithoutGlobal));
        }

        if (symbol.ContainingType is { } ct)
        {
            return new SymbolQualifiers(
                $"{ct.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}.{symbol.Name}",
                $"{ct.ToDisplayString(FullyQualifiedWithoutGlobal)}.{symbol.Name}");
        }

        return Bare(symbol.Name);
    }
}
