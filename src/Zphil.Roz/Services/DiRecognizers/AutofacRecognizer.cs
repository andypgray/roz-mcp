using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zphil.Roz.Services.DiRecognizers;

/// <summary>
///     Recognizes Autofac DI registrations. Lifetime is determined by walking the fluent chain
///     for calls like SingleInstance(), InstancePerLifetimeScope(), InstancePerDependency().
/// </summary>
internal sealed class AutofacRecognizer : NamespacePrefixRecognizer
{
    private static readonly Dictionary<string, string> LifetimeMethodMap = new(StringComparer.Ordinal)
    {
        ["SingleInstance"] = DiLifetimes.Singleton,
        ["InstancePerLifetimeScope"] = DiLifetimes.Scoped,
        ["InstancePerMatchingLifetimeScope"] = DiLifetimes.Scoped,
        ["InstancePerRequest"] = DiLifetimes.Scoped,
        ["InstancePerOwned"] = DiLifetimes.Scoped,
        ["InstancePerDependency"] = DiLifetimes.Transient
    };

    public override string ContainerName => "Autofac";
    protected override string NamespacePrefix => "Autofac";

    protected override HashSet<string> ResolutionMethods { get; } = new(StringComparer.Ordinal)
    {
        "Resolve", "ResolveOptional", "ResolveNamed", "ResolveKeyed",
        "TryResolve", "TryResolveNamed", "TryResolveKeyed",
        "BeginLifetimeScope", "Build", "Dispose"
    };

    public override string ExtractLifetime(InvocationExpressionSyntax invocation, IMethodSymbol method)
        => FluentChainHelper.MatchLifetime(invocation, LifetimeMethodMap, DiLifetimes.Transient);
}
