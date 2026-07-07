using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using Zphil.Roz.Extensions;
using Zphil.Roz.Models;
using Zphil.Roz.Services.DiRecognizers;

namespace Zphil.Roz.Services;

/// <summary>
///     Detects DI registration sites for a given type using Roslyn semantic analysis.
///     Supports multiple DI containers via pluggable <see cref="IDiContainerRecognizer" /> implementations.
/// </summary>
internal sealed class DiRegistrationScanner
{
    private static readonly IDiContainerRecognizer[] Recognizers =
    [
        new MediRecognizer(),
        new AutofacRecognizer(),
        new NinjectRecognizer(),
        new UnityRecognizer(),
        new SimpleInjectorRecognizer(),
        new DryIocRecognizer(),
        new LamarRecognizer(),
        new WindsorRecognizer()
    ];

    /// <summary>
    ///     Finds DI registrations for a single type.
    /// </summary>
    public async Task<IReadOnlyList<DiRegistration>> FindRegistrationsAsync(
        INamedTypeSymbol targetType, Solution solution, CancellationToken ct)
    {
        List<DiRegistration> results = [];

        IEnumerable<ReferencedSymbol> references = await SymbolFinder.FindReferencesAsync(
            targetType, solution, ct);

        foreach (ReferenceLocation refLoc in references.SelectMany(r => r.Locations))
        {
            ct.ThrowIfCancellationRequested();

            DiRegistration? registration = await TryExtractRegistrationAsync(refLoc, solution, ct);
            if (registration is not null)
            {
                results.Add(registration);
            }
        }

        return results.DistinctBy(r => (r.FilePath, r.Line)).ToList();
    }

    /// <summary>
    ///     Finds DI registrations for multiple types concurrently.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, IReadOnlyList<DiRegistration>>> FindRegistrationsForTypesAsync(
        IReadOnlyList<INamedTypeSymbol> types, Solution solution, CancellationToken ct)
    {
        IEnumerable<Task<(string Name, IReadOnlyList<DiRegistration> Registrations)>> tasks = types
            .Select(async type => (type.Name, await FindRegistrationsAsync(type, solution, ct)));

        (string Name, IReadOnlyList<DiRegistration> Registrations)[] results = await Task.WhenAll(tasks);

        Dictionary<string, IReadOnlyList<DiRegistration>> result = new();
        foreach ((string name, IReadOnlyList<DiRegistration> registrations) in results)
        {
            if (registrations.Count > 0)
            {
                result[name] = registrations;
            }
        }

        return result;
    }

    private static async Task<DiRegistration?> TryExtractRegistrationAsync(
        ReferenceLocation refLoc, Solution solution, CancellationToken ct)
    {
        if (!refLoc.Location.IsInSource)
        {
            return null;
        }

        Document? document = solution.GetDocument(refLoc.Location.SourceTree!);
        if (document is null)
        {
            return null;
        }

        SyntaxNode root = await refLoc.Location.SourceTree!.GetRootAsync(ct);
        SyntaxNode node = root.FindNode(refLoc.Location.SourceSpan);

        InvocationExpressionSyntax? invocation = node.FirstAncestorOrSelf<InvocationExpressionSyntax>();
        if (invocation is null)
        {
            return null;
        }

        SemanticModel? model = await document.GetSemanticModelAsync(ct);
        if (model is null)
        {
            return null;
        }

        IMethodSymbol? method = ResolveMethodSymbol(model, invocation, ct);
        if (method is null)
        {
            return null;
        }

        (IDiContainerRecognizer? recognizer, string lifetime) = MatchRecognizer(method, invocation);
        if (recognizer is null)
        {
            // Fallback: the invocation may live inside a builder/configurator lambda
            // (MassTransit/Quartz/MediatR/OpenTelemetry style). Check whether any enclosing
            // lambda's outer invocation — or its fluent-chain root — matches a recognizer.
            (IDiContainerRecognizer Recognizer, string Lifetime)? builderMatch =
                TryFindBuilderRecognizer(invocation, model, ct);
            if (builderMatch is null)
            {
                return null;
            }

            recognizer = builderMatch.Value.Recognizer;
            lifetime = builderMatch.Value.Lifetime;
        }

        SourceText text = root.GetText();
        int lineIndex = refLoc.Location.GetLineSpan().StartLinePosition.Line;
        string lineText = text.Lines[lineIndex].ToString().Trim();
        string projectName = ProjectExtensions.StripTfmSuffix(document.Project.Name);

        return new DiRegistration(
            lifetime,
            refLoc.Location.GetLineSpan().Path,
            lineIndex + 1,
            lineText,
            projectName,
            recognizer.ContainerName);
    }

    private static (IDiContainerRecognizer? Recognizer, string Lifetime) MatchRecognizer(
        IMethodSymbol method, InvocationExpressionSyntax invocation)
    {
        foreach (IDiContainerRecognizer recognizer in Recognizers)
        {
            if (recognizer.IsRegistrationInvocation(method))
            {
                return (recognizer, recognizer.ExtractLifetime(invocation, method));
            }
        }

        return (null, "");
    }

    private static IMethodSymbol? ResolveMethodSymbol(
        SemanticModel model, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        SymbolInfo symbolInfo = model.GetSymbolInfo(invocation, ct);
        return symbolInfo.Symbol as IMethodSymbol
               ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
    }

    /// <summary>
    ///     Walks outward through enclosing lambdas and backward along fluent chains looking
    ///     for an invocation that matches a known recognizer. Used to detect nested
    ///     builder/configurator registration patterns (MassTransit, Quartz, MediatR,
    ///     OpenTelemetry, etc.) where the inner invocation's method symbol lives on a
    ///     framework configurator type rather than IServiceCollection.
    /// </summary>
    private static (IDiContainerRecognizer Recognizer, string Lifetime)? TryFindBuilderRecognizer(
        InvocationExpressionSyntax innerInvocation, SemanticModel model, CancellationToken ct)
    {
        // DI registration APIs near-universally start with "Add" (AddConsumer, AddJob,
        // AddBehavior, AddSource, AddSaga, AddActivity). Configuration methods use
        // Set*/Use*/Configure*/With* prefixes. Applying this filter up-front avoids
        // false positives where any Add* inside a configure lambda would look like a
        // registration.
        if (!InvocationMethodNameStartsWith(innerInvocation, "Add"))
        {
            return null;
        }

        InvocationExpressionSyntax current = innerInvocation;
        // Depth 4 covers realistic DI builder nesting; deeper configurations are atypical and
        // bounding the walk prevents runaway traversal on pathological code.
        for (var depth = 0; depth < 4; depth++)
        {
            AnonymousFunctionExpressionSyntax? lambda = current.Parent?
                .FirstAncestorOrSelf<AnonymousFunctionExpressionSyntax>();
            if (lambda is null)
            {
                break;
            }

            InvocationExpressionSyntax? outer = lambda.Parent?
                .FirstAncestorOrSelf<InvocationExpressionSyntax>();
            if (outer is null)
            {
                break;
            }

            // Try matching the outer invocation directly (lambda-nesting pattern:
            // services.AddMassTransit(cfg => cfg.AddConsumer<T>()) — AddMassTransit matches).
            IDiContainerRecognizer? outerRecognizer = TryMatchInvocation(outer, model, ct);
            if (outerRecognizer is not null)
            {
                return (outerRecognizer, DiLifetimes.Scoped);
            }

            // Walk the fluent chain backward (method-chain pattern:
            // services.AddOpenTelemetry().WithTracing(t => t.AddSource<T>()) — WithTracing
            // doesn't match, but AddOpenTelemetry earlier in the chain does).
            InvocationExpressionSyntax chainNode = outer;
            while (chainNode.Expression is MemberAccessExpressionSyntax ma
                   && ma.Expression is InvocationExpressionSyntax prev)
            {
                IDiContainerRecognizer? chainRecognizer = TryMatchInvocation(prev, model, ct);
                if (chainRecognizer is not null)
                {
                    return (chainRecognizer, DiLifetimes.Scoped);
                }

                chainNode = prev;
            }

            current = outer;
        }

        return null;
    }

    private static IDiContainerRecognizer? TryMatchInvocation(
        InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken ct)
    {
        IMethodSymbol? method = ResolveMethodSymbol(model, invocation, ct);
        if (method is null)
        {
            return null;
        }

        return MatchRecognizer(method, invocation).Recognizer;
    }

    private static bool InvocationMethodNameStartsWith(InvocationExpressionSyntax invocation, string prefix)
    {
        SimpleNameSyntax? name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax ma => ma.Name,
            SimpleNameSyntax sn => sn,
            _ => null
        };

        return name?.Identifier.Text.StartsWith(prefix, StringComparison.Ordinal) == true;
    }
}
