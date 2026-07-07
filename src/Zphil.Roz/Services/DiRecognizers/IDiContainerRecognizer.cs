using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Zphil.Roz.Services.DiRecognizers;

/// <summary>
///     Identifies whether a method invocation is a DI registration for a specific container
///     and extracts the service lifetime.
/// </summary>
internal interface IDiContainerRecognizer
{
    public string ContainerName { get; }
    public bool IsRegistrationInvocation(IMethodSymbol method);
    public string ExtractLifetime(InvocationExpressionSyntax invocation, IMethodSymbol method);
}
