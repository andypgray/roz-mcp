using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zphil.Roz.Services.DiRecognizers;

/// <summary>
///     Recognizes Microsoft.Extensions.DependencyInjection registration methods.
///     Validates semantically: method must be in MEDI namespace with IServiceCollection as first param.
/// </summary>
internal sealed class MediRecognizer : IDiContainerRecognizer
{
    private static readonly HashSet<string> MediNamespaces = new(StringComparer.Ordinal)
    {
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.DependencyInjection.Extensions"
    };

    public string ContainerName => "MEDI";

    public bool IsRegistrationInvocation(IMethodSymbol method)
    {
        string? ns = method.ContainingType?.ContainingNamespace?.ToDisplayString();
        if (ns is null || !MediNamespaces.Contains(ns))
        {
            return false;
        }

        IMethodSymbol original = method.ReducedFrom ?? method;
        if (original.Parameters.Length == 0)
        {
            return false;
        }

        string firstParamType = original.Parameters[0].Type.Name;
        return firstParamType is "IServiceCollection" or "IKeyedServiceCollection";
    }

    public string ExtractLifetime(InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        string methodName = method.Name;

        if (methodName.Contains("Scoped", StringComparison.Ordinal))
        {
            return DiLifetimes.Scoped;
        }

        if (methodName.Contains("Transient", StringComparison.Ordinal))
        {
            return DiLifetimes.Transient;
        }

        if (methodName.Contains("Singleton", StringComparison.Ordinal))
        {
            return DiLifetimes.Singleton;
        }

        // MEDI's convenience registrations without a lifetime suffix (AddHttpClient,
        // AddOptions, etc.) register transient services — default accordingly. The explicit
        // Singleton check above must precede this so AddSingleton stays Singleton.
        return DiLifetimes.Transient;
    }
}
