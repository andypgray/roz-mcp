using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Zphil.Roz.Services.DiRecognizers;

namespace Zphil.Roz.Tests.Services;

/// <summary>
///     Unit tests for <see cref="SimpleInjectorRecognizer.ExtractLifetime" /> lifetime precedence
///     (CR-11c). The recognizer reads only the method name and the invocation's arguments, so a
///     parsed invocation plus a source-declared method symbol exercises it without a DI package.
/// </summary>
public class SimpleInjectorRecognizerTests
{
    private readonly SimpleInjectorRecognizer recognizer = new();

    [Fact]
    public void ExtractLifetime_SingletonNameButScopedLifestyleArg_LifestyleArgWins()
    {
        // Arrange — a registration whose method NAME contains "Singleton" yet passes an explicit
        // Lifestyle.Scoped argument. CR-11c reordered the heuristics so the argument wins.
        IMethodSymbol method = MethodNamed("RegisterSingleton");
        InvocationExpressionSyntax invocation = ParseInvocation("container.RegisterSingleton<Foo>(Lifestyle.Scoped)");

        // Act
        string lifetime = recognizer.ExtractLifetime(invocation, method);

        // Assert
        lifetime.ShouldBe(DiLifetimes.Scoped);
    }

    [Fact]
    public void ExtractLifetime_SingletonNameNoLifestyleArg_FallsBackToName()
    {
        // Arrange — no Lifestyle argument, so the method-name heuristic still applies (regression
        // guard that the reorder kept the name fallback for real RegisterSingleton<T>() calls).
        IMethodSymbol method = MethodNamed("RegisterSingleton");
        InvocationExpressionSyntax invocation = ParseInvocation("container.RegisterSingleton<Foo>()");

        // Act
        string lifetime = recognizer.ExtractLifetime(invocation, method);

        // Assert
        lifetime.ShouldBe(DiLifetimes.Singleton);
    }

    private static InvocationExpressionSyntax ParseInvocation(string expression) =>
        (InvocationExpressionSyntax)SyntaxFactory.ParseExpression(expression);

    private static IMethodSymbol MethodNamed(string name)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText($"class Holder {{ public void {name}() {{ }} }}");
        var compilation = CSharpCompilation.Create("recognizer-test", [tree]);
        INamedTypeSymbol holder = compilation.GetTypeByMetadataName("Holder")!;
        return holder.GetMembers(name).OfType<IMethodSymbol>().First();
    }
}
