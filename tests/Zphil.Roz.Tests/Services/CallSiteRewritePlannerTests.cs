using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Zphil.Roz.Models;
using Zphil.Roz.Services;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Tests.Services;

/// <summary>
///     Unit tests for <see cref="CallSiteRewritePlanner" /> — the pure-syntax argument-list rewrite and
///     its blockers. Uses a lightweight in-memory <see cref="CSharpCompilation" /> (no MSBuild workspace)
///     to obtain the old method symbol and bind the new parameter types.
/// </summary>
public class CallSiteRewritePlannerTests
{
    [Fact]
    public void Plan_Reorder_KeepsTextualOrderAddsNames()
    {
        RewritePlan plan = PlanFor("int M(int x, int y) => 0;", "(int y, int x)", "M(1, 2)");

        plan.Blocker.ShouldBeNull();
        plan.NewArgs!.ToString().ShouldBe("(x: 1, y: 2)");
    }

    [Fact]
    public void Plan_NonTrailingAddedOptional_NamesFollowing()
    {
        // Adding an optional b between a and c makes c bind by position 2 while only 1 arg precedes it.
        RewritePlan plan = PlanFor("int M(int a, int c) => 0;", "(int a, int b = 0, int c = 0)", "M(1, 2)");

        plan.Blocker.ShouldBeNull();
        plan.NewArgs!.ToString().ShouldBe("(1, c: 2)");
    }

    [Fact]
    public void Plan_RemovedArg_Dropped()
    {
        RewritePlan plan = PlanFor("int M(int a, int b, int c) => 0;", "(int a, int c)", "M(1, 2, 3)");

        plan.Blocker.ShouldBeNull();
        plan.NewArgs!.ToString().ShouldBe("(1, 3)");
        plan.DroppedArgs.Count.ShouldBe(1);
        plan.DroppedArgs[0].ToString().ShouldBe("2");
    }

    [Fact]
    public void Plan_TouchesParams_Blocker()
    {
        RewritePlan plan = PlanFor("int M(params int[] xs) => 0;", "(int head, params int[] xs)", "M(1, 2)");

        plan.Blocker.ShouldBe(RewriteBlocker.TouchesParamsArray);
    }

    [Fact]
    public void Plan_ReducedFormReceiver_Blocker()
    {
        // Reordering the receiver (parameter 0) breaks a reduced extension call.
        RewritePlan plan = PlanFor("int M(int self, int t) => 0;", "(int t, int self)", "M(1)", true);

        plan.Blocker.ShouldBe(RewriteBlocker.TouchesReceiverParam);
    }

    [Fact]
    public void Plan_AddRequired_NeedsCallerValue()
    {
        RewritePlan plan = PlanFor("int M(int a) => 0;", "(int a, int b)", "M(1)");

        plan.Blocker.ShouldBe(RewriteBlocker.NeedsCallerValue);
    }

    private static RewritePlan PlanFor(string methodDecl, string newSig, string call, bool reduced = false)
    {
        (IMethodSymbol method, SemanticModel model, int position) = Compile(methodDecl);
        ParsedSignature parsed = SignatureParser.Parse(newSig);
        List<SignatureParameter> newParams = parsed.Parameters
            .Select(p => p with
            {
                ResolvedType = model.GetSpeculativeTypeInfo(
                    position, p.TypeSyntax, SpeculativeBindingOption.BindAsTypeOrNamespace).Type
            })
            .ToList();
        SignatureDelta delta = SignatureDeltaComputer.Compute(method.Parameters, newParams);

        var invocation = (InvocationExpressionSyntax)SyntaxFactory.ParseExpression(call);
        return CallSiteRewritePlanner.Plan(invocation.ArgumentList, method, delta, reduced);
    }

    private static (IMethodSymbol Method, SemanticModel Model, int Position) Compile(string methodDecl)
    {
        var source = $"class C {{ {methodDecl} }}";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            "planner-test",
            [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]);
        SemanticModel model = compilation.GetSemanticModel(tree);
        IMethodSymbol method = compilation.GetTypeByMetadataName("C")!
            .GetMembers()
            .OfType<IMethodSymbol>()
            .First(m => m.MethodKind == MethodKind.Ordinary);
        int position = method.DeclaringSyntaxReferences[0].GetSyntax().SpanStart;
        return (method, model, position);
    }
}
