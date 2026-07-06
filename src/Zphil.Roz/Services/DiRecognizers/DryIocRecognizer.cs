using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zphil.Roz.Services.DiRecognizers;

/// <summary>
///     Recognizes DryIoc DI registrations. Lifetime is determined from the Reuse parameter
///     (e.g. Reuse.Singleton, Reuse.Scoped).
/// </summary>
internal sealed class DryIocRecognizer : NamespacePrefixRecognizer
{
    private static readonly Dictionary<string, string> ReuseNames = new(StringComparer.Ordinal)
    {
        ["Singleton"] = DiLifetimes.Singleton,
        ["Transient"] = DiLifetimes.Transient,
        ["Scoped"] = DiLifetimes.Scoped,
        ["ScopedOrSingleton"] = DiLifetimes.Scoped,
        ["InCurrentScope"] = DiLifetimes.Scoped,
        ["InCurrentNamedScope"] = DiLifetimes.Scoped,
        ["InWebRequest"] = DiLifetimes.Scoped
    };

    public override string ContainerName => "DryIoc";
    protected override string NamespacePrefix => "DryIoc";

    protected override HashSet<string> ResolutionMethods { get; } = new(StringComparer.Ordinal)
    {
        "Resolve", "ResolveMany", "Dispose", "OpenScope"
    };

    public override string ExtractLifetime(InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        foreach (ArgumentSyntax arg in invocation.ArgumentList.Arguments)
        {
            if (arg.Expression is MemberAccessExpressionSyntax memberAccess
                && ReuseNames.TryGetValue(memberAccess.Name.Identifier.Text, out string? lifetime))
            {
                return lifetime;
            }
        }

        return DiLifetimes.Transient;
    }
}
