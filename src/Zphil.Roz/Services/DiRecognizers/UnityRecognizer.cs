using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Services.DiRecognizers;

/// <summary>
///     Recognizes Unity DI container registrations. Lifetime is determined from the
///     lifetime manager type passed as a constructor argument (e.g. new ContainerControlledLifetimeManager()).
/// </summary>
internal sealed class UnityRecognizer : NamespacePrefixRecognizer
{
    private static readonly Dictionary<string, string> LifetimeManagerMap = new(StringComparer.Ordinal)
    {
        ["ContainerControlledLifetimeManager"] = DiLifetimes.Singleton,
        ["SingletonLifetimeManager"] = DiLifetimes.Singleton,
        ["TransientLifetimeManager"] = DiLifetimes.Transient,
        ["HierarchicalLifetimeManager"] = DiLifetimes.Scoped,
        ["PerResolveLifetimeManager"] = "PerResolve",
        ["PerThreadLifetimeManager"] = "Thread",
        ["ExternallyControlledLifetimeManager"] = "External"
    };

    public override string ContainerName => "Unity";
    protected override string NamespacePrefix => "Unity";

    protected override HashSet<string> ResolutionMethods { get; } = new(StringComparer.Ordinal)
    {
        "Resolve", "ResolveAll", "BuildUp", "Dispose", "CreateChildContainer"
    };

    public override bool IsRegistrationInvocation(IMethodSymbol method)
    {
        string? ns = method.ContainingType?.ContainingNamespace?.ToDisplayString();
        if (ns is null)
        {
            return false;
        }

        // Exclude Unity game engine namespaces — avoids double ToDisplayString() call from base
        if (ns.StartsWith("UnityEngine", StringComparison.Ordinal)
            || ns.StartsWith("UnityEditor", StringComparison.Ordinal))
        {
            return false;
        }

        return MatchesNamespacePrefix(ns)
               && !ResolutionMethods.Contains(method.Name);
    }

    public override string ExtractLifetime(InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        foreach (ArgumentSyntax arg in invocation.ArgumentList.Arguments)
        {
            if (arg.Expression is not ObjectCreationExpressionSyntax creation)
            {
                continue;
            }

            var typeName = creation.Type.ToString();
            string simpleName = FqnParser.SimpleName(typeName);

            if (LifetimeManagerMap.TryGetValue(simpleName, out string? lifetime))
            {
                return lifetime;
            }
        }

        return DiLifetimes.Transient;
    }
}
