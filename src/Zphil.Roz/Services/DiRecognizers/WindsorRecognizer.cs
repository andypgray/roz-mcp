using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zphil.Roz.Services.DiRecognizers;

/// <summary>
///     Recognizes Castle Windsor DI registrations. Lifetime is determined by walking the fluent
///     chain for method-based lifestyle calls (e.g. LifestyleSingleton(), LifestyleTransient()).
///     Property-based lifetime (e.g. .LifeStyle.Singleton) is not detected and defaults to Transient.
/// </summary>
internal sealed class WindsorRecognizer : NamespacePrefixRecognizer
{
    private static readonly Dictionary<string, string> LifetimeMethodMap = new(StringComparer.Ordinal)
    {
        ["LifestyleSingleton"] = DiLifetimes.Singleton,
        ["LifestyleTransient"] = DiLifetimes.Transient,
        ["LifestyleScoped"] = DiLifetimes.Scoped,
        ["LifestylePerWebRequest"] = DiLifetimes.Scoped,
        ["LifestylePerThread"] = "Thread",
        ["LifestylePooled"] = "Pooled",
        ["LifestyleBoundTo"] = "BoundTo",
        ["LifestyleCustom"] = "Custom"
    };

    private static readonly IReadOnlyList<string> WindsorNamespacePrefixes =
        ["Castle.MicroKernel", "Castle.Windsor"];

    public override string ContainerName => "Windsor";

    // Registration APIs live in both Castle.MicroKernel.Registration (Component.For<>()…) and
    // Castle.Windsor (IWindsorContainer.Register). Matching the bare "Castle" prefix would also
    // claim Castle.DynamicProxy/Castle.Core, so we enumerate the two real registration roots.
    protected override string NamespacePrefix => "Castle.Windsor";
    protected override IReadOnlyList<string> NamespacePrefixes => WindsorNamespacePrefixes;

    protected override HashSet<string> ResolutionMethods { get; } = new(StringComparer.Ordinal)
    {
        "Resolve", "ResolveAll", "Release", "Dispose",
        "AddChildContainer", "RemoveChildContainer"
    };

    public override string ExtractLifetime(InvocationExpressionSyntax invocation, IMethodSymbol method)
        => FluentChainHelper.MatchLifetime(invocation, LifetimeMethodMap, DiLifetimes.Transient);
}
