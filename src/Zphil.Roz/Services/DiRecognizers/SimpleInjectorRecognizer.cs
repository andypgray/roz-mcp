using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zphil.Roz.Services.DiRecognizers;

/// <summary>
///     Recognizes Simple Injector DI registrations. Lifetime is determined from the Lifestyle
///     parameter (e.g. Lifestyle.Singleton) or from method name suffixes (e.g. RegisterSingleton).
/// </summary>
internal sealed class SimpleInjectorRecognizer : NamespacePrefixRecognizer
{
    private static readonly Dictionary<string, string> LifestyleNames = new(StringComparer.Ordinal)
    {
        ["Singleton"] = DiLifetimes.Singleton,
        ["Transient"] = DiLifetimes.Transient,
        ["Scoped"] = DiLifetimes.Scoped
    };

    public override string ContainerName => "SimpleInjector";
    protected override string NamespacePrefix => "SimpleInjector";

    protected override HashSet<string> ResolutionMethods { get; } = new(StringComparer.Ordinal)
    {
        "GetInstance", "GetAllInstances", "GetRegistration", "Verify", "Dispose"
    };

    public override string ExtractLifetime(InvocationExpressionSyntax invocation, IMethodSymbol method)
    {
        // An explicit Lifestyle argument is authoritative — inspect it before falling back to
        // the method-name heuristic: container.Register<T>(Lifestyle.Singleton). When both signals
        // are present, the argument wins (a RegisterSingleton-named call passing Lifestyle.Scoped
        // is Scoped, not Singleton).
        foreach (ArgumentSyntax arg in invocation.ArgumentList.Arguments)
        {
            if (arg.Expression is MemberAccessExpressionSyntax memberAccess
                && LifestyleNames.TryGetValue(memberAccess.Name.Identifier.Text, out string? lifetime))
            {
                return lifetime;
            }
        }

        // Method name may encode lifetime: RegisterSingleton<T>()
        if (method.Name.Contains("Singleton", StringComparison.Ordinal))
        {
            return DiLifetimes.Singleton;
        }

        return DiLifetimes.Transient;
    }
}
