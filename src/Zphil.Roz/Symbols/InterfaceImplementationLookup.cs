using Microsoft.CodeAnalysis;

namespace Zphil.Roz.Symbols;

/// <summary>
///     Finds the interface member(s) that a concrete class/struct member implements.
///     Covers both implicit implementations (a public member matching an interface member by name)
///     and explicit implementations (<c>void IFoo.Bar() {}</c>).
/// </summary>
internal static class InterfaceImplementationLookup
{
    /// <summary>
    ///     Returns every interface member that <paramref name="member" /> implements — explicitly or implicitly.
    /// </summary>
    /// <remarks>
    ///     Returns an empty list when the containing type has no interfaces, when the symbol itself is declared
    ///     on an interface, or when no interface member resolves to <paramref name="member" />.
    /// </remarks>
    internal static IReadOnlyList<ISymbol> FindInterfaceMembers(ISymbol member)
    {
        if (member.ContainingType is null || member.ContainingType.AllInterfaces.Length == 0)
        {
            return [];
        }

        List<ISymbol> results = [];

        IEnumerable<ISymbol> explicitImpls = member switch
        {
            IMethodSymbol m => m.ExplicitInterfaceImplementations,
            IPropertySymbol p => p.ExplicitInterfaceImplementations,
            IEventSymbol e => e.ExplicitInterfaceImplementations,
            _ => []
        };
        results.AddRange(explicitImpls);

        foreach (INamedTypeSymbol iface in member.ContainingType.AllInterfaces)
        foreach (ISymbol ifaceMember in iface.GetMembers(member.Name))
        {
            ISymbol? impl = member.ContainingType.FindImplementationForInterfaceMember(ifaceMember);
            if (SymbolEqualityComparer.Default.Equals(impl, member)
                && !results.Contains(ifaceMember, SymbolEqualityComparer.Default))
            {
                results.Add(ifaceMember);
            }
        }

        return results;
    }

    /// <summary>
    ///     Returns the first interface member that <paramref name="member" /> implements, or <c>null</c> if none.
    /// </summary>
    internal static ISymbol? FindInterfaceMember(ISymbol member) =>
        FindInterfaceMembers(member).FirstOrDefault();
}
