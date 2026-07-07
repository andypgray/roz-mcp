using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zphil.Roz.Services.DiRecognizers;

/// <summary>
///     Recognizes Ninject DI registrations. Lifetime is determined by walking the fluent chain
///     for calls like InSingletonScope(), InTransientScope(), InRequestScope().
/// </summary>
internal sealed class NinjectRecognizer : NamespacePrefixRecognizer
{
    private static readonly Dictionary<string, string> LifetimeMethodMap = new(StringComparer.Ordinal)
    {
        ["InSingletonScope"] = DiLifetimes.Singleton,
        ["InTransientScope"] = DiLifetimes.Transient,
        ["InRequestScope"] = DiLifetimes.Scoped,
        ["InThreadScope"] = "Thread"
    };

    public override string ContainerName => "Ninject";
    protected override string NamespacePrefix => "Ninject";

    protected override HashSet<string> ResolutionMethods { get; } = new(StringComparer.Ordinal)
    {
        "Get", "GetAll", "TryGet", "TryGetAndThrowOnInvalidBinding",
        "Dispose"
    };

    public override string ExtractLifetime(InvocationExpressionSyntax invocation, IMethodSymbol method)
        => FluentChainHelper.MatchLifetime(invocation, LifetimeMethodMap, DiLifetimes.Transient);
}
