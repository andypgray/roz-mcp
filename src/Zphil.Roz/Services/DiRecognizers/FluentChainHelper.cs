using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zphil.Roz.Services.DiRecognizers;

/// <summary>
///     Walks fluent method chains in the syntax tree to find lifetime-indicating calls
///     for containers like Autofac, Ninject, and Lamar.
/// </summary>
internal static class FluentChainHelper
{
    /// <summary>
    ///     From a starting invocation, walks up the syntax tree collecting method names
    ///     from the enclosing fluent chain. Yields each chained method name in order
    ///     from innermost to outermost.
    /// </summary>
    private static IEnumerable<string> GetChainedMethodNames(InvocationExpressionSyntax startInvocation)
    {
        SyntaxNode? node = startInvocation.Parent;
        while (node is MemberAccessExpressionSyntax memberAccess)
        {
            yield return memberAccess.Name.Identifier.Text;
            node = memberAccess.Parent is InvocationExpressionSyntax inv ? inv.Parent : null;
        }
    }

    /// <summary>
    ///     Searches the fluent chain above an invocation for a method name matching
    ///     a known lifetime method, returning the corresponding lifetime string.
    /// </summary>
    internal static string MatchLifetime(
        InvocationExpressionSyntax invocation,
        IReadOnlyDictionary<string, string> lifetimeMethods,
        string defaultLifetime)
    {
        foreach (string methodName in GetChainedMethodNames(invocation))
        {
            if (lifetimeMethods.TryGetValue(methodName, out string? lifetime))
            {
                return lifetime;
            }
        }

        return defaultLifetime;
    }
}
