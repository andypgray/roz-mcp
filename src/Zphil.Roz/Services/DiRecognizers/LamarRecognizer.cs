using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zphil.Roz.Services.DiRecognizers;

/// <summary>
///     Recognizes Lamar/StructureMap DI registrations. Lifetime is determined by walking the
///     fluent chain for calls like Singleton(), Transient(), Scoped().
///     Note: Lamar also supports MEDI-style IServiceCollection methods, which are handled
///     by MediRecognizer separately.
/// </summary>
internal sealed class LamarRecognizer : NamespacePrefixRecognizer
{
    private static readonly IReadOnlyList<string> LamarNamespacePrefixes = ["Lamar", "StructureMap"];

    private static readonly Dictionary<string, string> LifetimeMethodMap = new(StringComparer.Ordinal)
    {
        ["Singleton"] = DiLifetimes.Singleton,
        ["Transient"] = DiLifetimes.Transient,
        ["Scoped"] = DiLifetimes.Scoped
    };

    public override string ContainerName => "Lamar";

    // Registrations span both the Lamar and StructureMap namespaces. Bare-prefix matching would
    // also claim user namespaces like LamarHelpers / StructureMapExtensions, so go through the
    // dotted-boundary helper (same fix as Windsor/Ninject/Unity).
    protected override string NamespacePrefix => "Lamar";
    protected override IReadOnlyList<string> NamespacePrefixes => LamarNamespacePrefixes;

    // Lamar registration is determined purely by namespace — there is no resolution-method blacklist.
    protected override HashSet<string> ResolutionMethods { get; } = new(StringComparer.Ordinal);

    public override string ExtractLifetime(InvocationExpressionSyntax invocation, IMethodSymbol method)
        => FluentChainHelper.MatchLifetime(invocation, LifetimeMethodMap, DiLifetimes.Transient);
}
