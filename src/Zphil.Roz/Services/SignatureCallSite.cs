using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zphil.Roz.Services;

/// <summary>
///     Shared syntax helpers for locating a signature-change reference's enclosing call node and its
///     argument list. Used by both precise analysis (<see cref="SignatureImpactAnalyzer" />) and apply
///     (<see cref="ChangeSignatureService" />) so the two agree on what counts as a call site.
/// </summary>
internal static class SignatureCallSite
{
    /// <summary>
    ///     Climbs from a reference identifier to the enclosing call-like node — an invocation, object
    ///     creation, constructor initializer (<c>: base(...)</c>/<c>: this(...)</c>), or attribute usage —
    ///     stopping (returning null) at the first enclosing statement or member declaration.
    /// </summary>
    public static SyntaxNode? ResolveCallNode(SyntaxNode? node)
    {
        for (SyntaxNode? current = node; current is not null; current = current.Parent)
        {
            switch (current)
            {
                case InvocationExpressionSyntax:
                case ObjectCreationExpressionSyntax:
                case ImplicitObjectCreationExpressionSyntax:
                case ConstructorInitializerSyntax:
                case AttributeSyntax:
                    return current;
                case StatementSyntax:
                case MemberDeclarationSyntax:
                    return null;
            }
        }

        return null;
    }

    /// <summary>
    ///     Returns the <see cref="BaseArgumentListSyntax" /> of a call node, or null for an attribute
    ///     (which carries an <see cref="AttributeArgumentListSyntax" />, handled conservatively upstream).
    /// </summary>
    public static BaseArgumentListSyntax? ArgumentListOf(SyntaxNode call) => call switch
    {
        InvocationExpressionSyntax i => i.ArgumentList,
        ObjectCreationExpressionSyntax o => o.ArgumentList,
        ImplicitObjectCreationExpressionSyntax ic => ic.ArgumentList,
        ConstructorInitializerSyntax ci => ci.ArgumentList,
        _ => null
    };
}
