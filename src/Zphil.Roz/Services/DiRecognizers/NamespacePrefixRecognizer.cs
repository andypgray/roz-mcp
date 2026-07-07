using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zphil.Roz.Services.DiRecognizers;

/// <summary>
///     Base class for DI container recognizers that identify registrations by namespace prefix
///     and exclude known resolution methods via a blacklist.
/// </summary>
/// <remarks>
///     The blacklist approach is intentional: registration APIs are extensive and vary across
///     container versions, so a whitelist would be brittle. Resolution methods are few and stable.
///     The blacklists are best-effort — uncommon resolution methods may not be excluded.
/// </remarks>
internal abstract class NamespacePrefixRecognizer : IDiContainerRecognizer
{
    protected abstract string NamespacePrefix { get; }

    /// <summary>
    ///     Namespaces this recognizer claims. Defaults to the single <see cref="NamespacePrefix" />;
    ///     override to match a container whose registration APIs span multiple root namespaces
    ///     (e.g. Castle Windsor's <c>Castle.MicroKernel</c> + <c>Castle.Windsor</c>).
    /// </summary>
    protected virtual IReadOnlyList<string> NamespacePrefixes => [NamespacePrefix];

    protected abstract HashSet<string> ResolutionMethods { get; }
    public abstract string ContainerName { get; }

    public virtual bool IsRegistrationInvocation(IMethodSymbol method)
    {
        string? ns = method.ContainingType?.ContainingNamespace?.ToDisplayString();
        return ns is not null
               && MatchesNamespacePrefix(ns)
               && !ResolutionMethods.Contains(method.Name);
    }

    public abstract string ExtractLifetime(InvocationExpressionSyntax invocation, IMethodSymbol method);

    /// <summary>
    ///     Matches <paramref name="ns" /> against <see cref="NamespacePrefixes" /> on dotted
    ///     namespace boundaries: a prefix matches only the namespace itself or a descendant
    ///     (<c>prefix.*</c>), never a sibling that merely shares a leading substring. This is
    ///     what keeps <c>Castle.DynamicProxy</c> out of Windsor and <c>NinjectHelpers</c> out
    ///     of Ninject.
    /// </summary>
    protected bool MatchesNamespacePrefix(string ns) =>
        NamespacePrefixes.Any(p => ns.StartsWith(p, StringComparison.Ordinal)
                                   && (ns.Length == p.Length || ns[p.Length] == '.'));
}
