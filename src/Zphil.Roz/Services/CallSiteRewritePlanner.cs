using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Zphil.Roz.Models;

namespace Zphil.Roz.Services;

/// <summary>
///     Pure-syntax planner that rewrites one call site's argument list under a deterministic signature
///     change (add-with-default / remove / reorder). Shared by precise analysis (matrix row for a
///     failed re-bind that a rewrite would fix) and by apply. Returns a <see cref="RewriteBlocker" />
///     when the site is outside the mechanically-appliable subset.
/// </summary>
/// <remarks>
///     Argument expressions are never physically reordered — original left-to-right order is preserved
///     (so side-effect order is unchanged) and name-colons are attached instead. Names are minimal: an
///     argument stays positional while the leading positional run still binds 1:1 to the leading new
///     parameters; from the first divergence onward every argument is named.
/// </remarks>
internal static class CallSiteRewritePlanner
{
    /// <summary>
    ///     Plans the rewrite of <paramref name="oldArgs" /> for a call bound to
    ///     <paramref name="boundMethod" /> under <paramref name="delta" />.
    ///     <paramref name="reducedExtensionForm" /> is per-site: true for <c>x.M(a)</c> (the receiver is
    ///     parameter 0, not an argument), false for the static form <c>C.M(x, a)</c>.
    /// </summary>
    public static RewritePlan Plan(
        BaseArgumentListSyntax oldArgs, IMethodSymbol boundMethod, SignatureDelta delta, bool reducedExtensionForm)
    {
        if (delta.TouchesParams)
        {
            return Blocked(RewriteBlocker.TouchesParamsArray);
        }

        if (delta.Added.Any(a => !a.HasExplicitDefault))
        {
            return Blocked(RewriteBlocker.NeedsCallerValue);
        }

        IReadOnlyList<IParameterSymbol> oldParameters = boundMethod.Parameters;
        int receiverOffset = reducedExtensionForm ? 1 : 0;

        if (reducedExtensionForm && oldParameters.Count > 0 && TouchesParameter(delta, oldParameters[0]))
        {
            return Blocked(RewriteBlocker.TouchesReceiverParam);
        }

        // Map each written argument to the old parameter it binds.
        List<ArgumentSyntax> arguments = oldArgs.Arguments.ToList();
        Dictionary<ArgumentSyntax, IParameterSymbol> argToOld = new();
        for (var i = 0; i < arguments.Count; i++)
        {
            ArgumentSyntax argument = arguments[i];
            if (argument.NameColon?.Name.Identifier.ValueText is { } argName)
            {
                IParameterSymbol? named = oldParameters.FirstOrDefault(p => p.Name == argName);
                if (named is null)
                {
                    return Blocked(RewriteBlocker.ExpandedParamsForm);
                }

                argToOld[argument] = named;
                continue;
            }

            int paramIndex = i + receiverOffset;
            if (paramIndex >= oldParameters.Count)
            {
                return Blocked(RewriteBlocker.ExpandedParamsForm);
            }

            IParameterSymbol positional = oldParameters[paramIndex];
            if (positional.IsParams)
            {
                // A positional argument landing on a params parameter is either array- or expanded-form;
                // either way it is outside the deterministic subset (TouchesParams already guards real
                // params-deltas — this is the defensive backstop).
                return Blocked(RewriteBlocker.ExpandedParamsForm);
            }

            argToOld[argument] = positional;
        }

        HashSet<int> removedOrdinals = delta.Removed.Select(p => p.Ordinal).ToHashSet();

        // Walk the arguments in original order; drop removed, name from the first divergence onward.
        // Divergence = a positional argument that would bind the wrong new parameter, or any named
        // argument (C# requires named args to precede any later positional, so once one appears every
        // following argument is named). Parameters are never renamed, so the new name is old.Name.
        List<ArgumentSyntax> emitted = [];
        List<ArgumentSyntax> dropped = [];
        int positionalCount = receiverOffset;
        var mustName = false;
        foreach (ArgumentSyntax argument in arguments)
        {
            IParameterSymbol old = argToOld[argument];
            if (removedOrdinals.Contains(old.Ordinal))
            {
                dropped.Add(argument);
                continue;
            }

            if (!delta.NewOrdinalByOldOrdinal.TryGetValue(old.Ordinal, out int newOrdinal))
            {
                // A non-removed old parameter must be kept or retyped; a miss means the delta and the
                // site's bound member disagree about the slot layout — an internal invariant break,
                // never a user-fixable call shape. Fail loudly instead of mislabeling the site with a
                // params blocker the user would fruitlessly hunt for.
                throw new InvalidOperationException(
                    $"Parameter '{old.Name}' (ordinal {old.Ordinal}) is neither kept, retyped, nor removed in the signature delta.");
            }

            if (!mustName && argument.NameColon is null && newOrdinal == positionalCount)
            {
                emitted.Add(StripOuterTrivia(argument));
                positionalCount++;
                continue;
            }

            mustName = true;
            emitted.Add(NameArgument(argument, old.Name));
        }

        return new RewritePlan(BuildArgumentList(oldArgs, emitted), null, dropped);
    }

    private static RewritePlan Blocked(RewriteBlocker blocker) => new(null, blocker, []);

    private static bool TouchesParameter(SignatureDelta delta, IParameterSymbol parameter)
    {
        // Ordinal comparison, not symbol identity: `parameter` belongs to the site's bound member,
        // the delta's lists to the anchor member — different symbols, same slot layout.
        if (delta.Removed.Any(r => r.Ordinal == parameter.Ordinal))
        {
            return true;
        }

        if (delta.Retyped.Any(t => t.Old.Ordinal == parameter.Ordinal))
        {
            return true;
        }

        return delta.NewOrdinalByOldOrdinal.TryGetValue(parameter.Ordinal, out int newOrdinal)
               && newOrdinal != parameter.Ordinal;
    }

    private static ArgumentSyntax NameArgument(ArgumentSyntax argument, string paramName)
    {
        if (argument.NameColon is not null)
        {
            return StripOuterTrivia(argument);
        }

        NameColonSyntax nameColon = SyntaxFactory.NameColon(paramName)
            .WithTrailingTrivia(SyntaxFactory.Space);
        return StripOuterTrivia(argument).WithNameColon(nameColon);
    }

    private static ArgumentSyntax StripOuterTrivia(ArgumentSyntax argument) =>
        argument.WithoutTrivia();

    private static ArgumentListSyntax BuildArgumentList(BaseArgumentListSyntax oldArgs, List<ArgumentSyntax> args)
    {
        List<SyntaxNodeOrToken> nodesAndTokens = [];
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0)
            {
                nodesAndTokens.Add(SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space));
            }

            nodesAndTokens.Add(args[i]);
        }

        SeparatedSyntaxList<ArgumentSyntax> separated = SyntaxFactory.SeparatedList<ArgumentSyntax>(nodesAndTokens);
        return SyntaxFactory.ArgumentList(separated).WithTriviaFrom(oldArgs);
    }
}
