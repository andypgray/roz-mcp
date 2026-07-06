using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Zphil.Roz.Enums;

namespace Zphil.Roz.Services;

/// <summary>
///     Classifies Roslyn reference locations as invocations, reads, or writes based on
///     the surrounding syntax. Invocations are detected via the enclosing <c>InvocationExpressionSyntax</c>
///     / <c>ObjectCreationExpressionSyntax</c>; writes are detected via assignment LHS, <c>out</c>/<c>ref</c>
///     arguments, <c>++</c>/<c>--</c>, and deconstruction targets.
/// </summary>
internal static class ReferenceKindClassifier
{
    /// <summary>
    ///     Filters <paramref name="locations" /> to those matching <paramref name="kinds" />.
    /// </summary>
    /// <remarks>
    ///     When <paramref name="kinds" /> is <see cref="ReferenceKind.All" /> the input is returned unchanged.
    /// </remarks>
    public static List<ReferenceLocation> Filter(
        IEnumerable<ReferenceLocation> locations,
        ReferenceKind kinds,
        CancellationToken ct)
    {
        if (kinds == ReferenceKind.All)
        {
            return locations.ToList();
        }

        Dictionary<SyntaxTree, SyntaxNode> rootCache = new();
        List<ReferenceLocation> filtered = new();

        foreach (ReferenceLocation loc in locations)
        {
            ct.ThrowIfCancellationRequested();

            SyntaxTree? tree = loc.Location.SourceTree;
            if (tree is null)
            {
                continue;
            }

            if (!rootCache.TryGetValue(tree, out SyntaxNode? root))
            {
                root = tree.GetRoot(ct);
                rootCache[tree] = root;
            }

            SyntaxNode node = root.FindNode(loc.Location.SourceSpan, getInnermostNodeForTie: true);

            ReferenceRole role = Classify(node);

            bool matches = kinds switch
            {
                ReferenceKind.Invocations => role == ReferenceRole.Invocation,
                ReferenceKind.Writes => role == ReferenceRole.Write,
                ReferenceKind.Reads => role == ReferenceRole.Read,
                _ => true
            };

            if (matches)
            {
                filtered.Add(loc);
            }
        }

        return filtered;
    }

    /// <summary>
    ///     Classifies a single reference node as a read, write, or invocation based on its
    ///     surrounding syntax. <paramref name="node" /> is the node found at the reference span
    ///     (e.g. via <c>root.FindNode(span, getInnermostNodeForTie: true)</c>).
    /// </summary>
    /// <remarks>
    ///     The read/write/invocation distinction is exactly the value-flow direction
    ///     <c>analyze_change_impact</c> needs: an invocation or read is a <em>producer</em> (the
    ///     symbol's value flows outward to a consumer context), a write is a <em>consumer</em>
    ///     (a value flows inward into the symbol). Sharing one classifier keeps both tools'
    ///     notions of "what is this reference doing" identical.
    /// </remarks>
    public static ReferenceRole Classify(SyntaxNode node)
    {
        if (IsInvocationContext(node))
        {
            return ReferenceRole.Invocation;
        }

        return IsWriteContext(node) ? ReferenceRole.Write : ReferenceRole.Read;
    }

    /// <summary>
    ///     True when <paramref name="identifierNode" /> is the target of a write: assignment LHS
    ///     (including compound, object/<c>with</c> initializer), <c>++</c>/<c>--</c>, <c>out</c>/<c>ref</c>
    ///     argument, or deconstruction target.
    /// </summary>
    private static bool IsWriteContext(SyntaxNode identifierNode)
    {
        SyntaxNode current = Unwrap(identifierNode);

        switch (current.Parent)
        {
            case AssignmentExpressionSyntax assignment when assignment.Left == current:
                return true;

            case PostfixUnaryExpressionSyntax postfix
                when postfix.Operand == current
                     && (postfix.IsKind(SyntaxKind.PostIncrementExpression)
                         || postfix.IsKind(SyntaxKind.PostDecrementExpression)):
                return true;

            case PrefixUnaryExpressionSyntax prefix
                when prefix.Operand == current
                     && (prefix.IsKind(SyntaxKind.PreIncrementExpression) || prefix.IsKind(SyntaxKind.PreDecrementExpression)):
                return true;

            case ArgumentSyntax refArg
                when refArg.Expression == current
                     && (refArg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword)
                         || refArg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword)):
                return true;

            case DeclarationExpressionSyntax:
                return true;
        }

        return IsDeconstructionTarget(current);
    }

    /// <summary>
    ///     True when <paramref name="node" /> sits inside one or more nested tuple expressions
    ///     that ultimately form the LHS of a deconstruction assignment:
    ///     <c>(a, b) = …</c>, <c>((a, b), c) = …</c>, etc.
    /// </summary>
    private static bool IsDeconstructionTarget(SyntaxNode node)
    {
        if (node.Parent is not ArgumentSyntax arg || arg.Expression != node || !arg.RefKindKeyword.IsKind(SyntaxKind.None))
        {
            return false;
        }

        SyntaxNode? tuple = arg.Parent;
        while (tuple is TupleExpressionSyntax currentTuple)
        {
            if (currentTuple.Parent is AssignmentExpressionSyntax assignment && assignment.Left == currentTuple)
            {
                return true;
            }

            if (currentTuple.Parent is ArgumentSyntax outerArg && outerArg.Parent is TupleExpressionSyntax outerTuple)
            {
                tuple = outerTuple;
                continue;
            }

            break;
        }

        return false;
    }

    private static bool IsInvocationContext(SyntaxNode identifierNode)
    {
        SyntaxNode current = Unwrap(identifierNode);

        return current.Parent switch
        {
            InvocationExpressionSyntax inv when inv.Expression == current => !IsNameofInvocation(inv),
            ObjectCreationExpressionSyntax oc when oc.Type == current => true,
            ImplicitObjectCreationExpressionSyntax => true,
            _ => false
        };
    }

    /// <summary>
    ///     True when <paramref name="inv" /> is a <c>nameof(...)</c> expression — it parses as an
    ///     <c>InvocationExpressionSyntax</c> but is a compile-time symbol reference, not a runtime call.
    /// </summary>
    internal static bool IsNameofInvocation(InvocationExpressionSyntax inv) =>
        inv.Expression is IdentifierNameSyntax { Identifier.Text: "nameof" };

    /// <summary>
    ///     True when <paramref name="node" /> sits inside a <c>nameof(...)</c> operand — a compile-time
    ///     symbol reference, not a runtime use. <c>Classify</c> returns <see cref="ReferenceRole.Read" />
    ///     for both method groups and nameof operands, so a SignatureChange caller needs this to tell
    ///     the two apart; <see cref="OutboundCallExtractor" /> uses it to skip nameof operands entirely.
    /// </summary>
    internal static bool IsNameofOperand(SyntaxNode node)
    {
        for (SyntaxNode? current = node; current is not null; current = current.Parent)
        {
            if (current is InvocationExpressionSyntax inv && IsNameofInvocation(inv))
            {
                return true;
            }

            // Bound the walk — nameof is reached first if present; beyond the enclosing
            // statement/member the node cannot be a nameof operand.
            if (current is StatementSyntax or MemberDeclarationSyntax)
            {
                break;
            }
        }

        return false;
    }

    /// <summary>
    ///     If the identifier is the <c>Name</c> portion of a <c>MemberAccessExpressionSyntax</c> or
    ///     <c>MemberBindingExpressionSyntax</c>, return the wrapping expression so downstream classification
    ///     sees <c>obj.Field</c> / <c>obj?.Field</c> rather than just <c>Field</c>.
    /// </summary>
    private static SyntaxNode Unwrap(SyntaxNode node)
    {
        return node.Parent switch
        {
            MemberAccessExpressionSyntax ma when ma.Name == node => ma,
            MemberBindingExpressionSyntax mb when mb.Name == node => mb,
            _ => node
        };
    }
}
