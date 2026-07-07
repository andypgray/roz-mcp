using Microsoft.CodeAnalysis;
using Zphil.Roz.Enums;
using Zphil.Roz.Extensions;
using Zphil.Roz.Models;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Services;

/// <summary>
///     Service encapsulating type hierarchy lookups (base classes + interfaces).
/// </summary>
internal sealed class TypeHierarchyService(SymbolResolver symbolResolver)
{
    /// <summary>
    ///     Gets the full inheritance chain for a type: base classes and implemented interfaces.
    /// </summary>
    public async Task<TypeHierarchyResult> GetTypeHierarchyAsync(
        string? filePath, int? line, int? column,
        string? symbolName = null, string? containingType = null,
        SymbolicKind? kind = null, string? project = null, bool excludeTests = false,
        CancellationToken ct = default)
    {
        Func<ISymbol, bool> filter = kind.HasValue
            ? s => s is INamedTypeSymbol && s.MatchesKindFilter(kind)
            : static s => s is INamedTypeSymbol;
        string filterDesc = kind?.ToString().ToLowerInvariant() ?? "type";

        (_, string solutionDir, ISymbol symbol) =
            await symbolResolver.ResolveSymbolAsync(filePath, line, column, symbolName, containingType, excludeTests,
                filter, filterDesc, project, ct, kind);

        if (symbol is not INamedTypeSymbol typeSymbol)
        {
            throw new UserErrorException(
                $"'{symbol.Name}' is a {symbol.GetKindString()}, not a type. Position on a class, struct, or interface.");
        }

        return new TypeHierarchyResult(typeSymbol, solutionDir);
    }
}
