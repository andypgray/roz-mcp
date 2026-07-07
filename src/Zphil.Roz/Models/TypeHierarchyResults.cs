using Microsoft.CodeAnalysis;

namespace Zphil.Roz.Models;

internal sealed record TypeHierarchyResult(
    INamedTypeSymbol TypeSymbol,
    string SolutionDir);
