using Microsoft.CodeAnalysis;
using Zphil.Roz.Symbols;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Symbols;

/// <summary>
///     Branch-coverage unit tests for <see cref="NameMatchPipeline.DispatchAsync" />. Exercises
///     each dispatch branch (name search, FQN, FQN-miss fallthrough, special member with and
///     without file inference) and verifies the branch flags on <see cref="NameMatchDispatchResult" />.
/// </summary>
public class NameMatchPipelineTests(WorkspaceFixture fixture)
{
    [Fact]
    public async Task Dispatch_BareName_UsesNameSearch()
    {
        // Arrange
        (Solution solution, IReadOnlyList<Project> projects) = await GetSolutionAndProjectsAsync();

        // Act
        NameMatchDispatchResult result = await NameMatchPipeline.DispatchAsync(
            solution, projects, "Circle", null,
            null, null,
            false, TestContext.Current.CancellationToken);

        // Assert
        result.SearchName.ShouldBe("Circle");
        result.RequestedArity.ShouldBeNull();
        result.IsFqnMatched.ShouldBeFalse();
        result.IsSpecialMember.ShouldBeFalse();
        result.Candidates.ShouldContain(s => s.Name == "Circle");
    }

    [Fact]
    public async Task Dispatch_Fqn_UsesFqnResolver_AndSetsFqnMatched()
    {
        // Arrange
        (Solution solution, IReadOnlyList<Project> projects) = await GetSolutionAndProjectsAsync();

        // Act
        NameMatchDispatchResult result = await NameMatchPipeline.DispatchAsync(
            solution, projects, "TestFixture.Shapes.Circle", null,
            null, null,
            false, TestContext.Current.CancellationToken);

        // Assert
        result.SearchName.ShouldBe("TestFixture.Shapes.Circle");
        result.IsFqnMatched.ShouldBeTrue();
        result.IsSpecialMember.ShouldBeFalse();
        result.Candidates.ShouldContain(s => s is INamedTypeSymbol && s.Name == "Circle");
    }

    [Fact]
    public async Task Dispatch_FqnMissesAndFallsThrough_ToNameSearch()
    {
        // Arrange — FQN-shaped name where no type "Foo" exists, so FqnResolver returns empty
        // and the pipeline falls through to SymbolSearch (which also returns nothing for
        // a dotted pattern). IsFqnMatched stays false either way.
        (Solution solution, IReadOnlyList<Project> projects) = await GetSolutionAndProjectsAsync();

        // Act
        NameMatchDispatchResult result = await NameMatchPipeline.DispatchAsync(
            solution, projects, "Foo.NonExistent", null,
            null, null,
            false, TestContext.Current.CancellationToken);

        // Assert — FqnResolver missed and SymbolSearch found nothing for the dotted pattern, so the
        // fallthrough path yields zero candidates (not just the unchanged branch flags).
        result.IsFqnMatched.ShouldBeFalse();
        result.IsSpecialMember.ShouldBeFalse();
        result.Candidates.ShouldBeEmpty();
    }

    [Fact]
    public async Task Dispatch_SpecialMember_NoFileInference_RequiresContainingType()
    {
        // Arrange
        (Solution solution, IReadOnlyList<Project> projects) = await GetSolutionAndProjectsAsync();

        // Act
        NameMatchDispatchResult result = await NameMatchPipeline.DispatchAsync(
            solution, projects, ".ctor", "Circle",
            null, null,
            false, TestContext.Current.CancellationToken);

        // Assert
        result.SearchName.ShouldBe(".ctor");
        result.IsSpecialMember.ShouldBeTrue();
        result.IsFqnMatched.ShouldBeFalse();
        result.ResolvedContainingType.ShouldBe("Circle");
        result.Candidates.ShouldAllBe(s => (s as IMethodSymbol)!.MethodKind == MethodKind.Constructor);
        result.Candidates.ShouldAllBe(s => s.ContainingType!.Name == "Circle");
    }

    [Fact]
    public async Task Dispatch_SpecialMember_WithFileInference_InfersType()
    {
        // Arrange — Circle.cs declares a single type "Circle". With useFileInference=true and
        // no containingType, the pipeline should infer "Circle" from the file's declarations.
        (Solution solution, IReadOnlyList<Project> projects) = await GetSolutionAndProjectsAsync();
        string circlePath = fixture.ShapesFile("Circle.cs");

        // Act
        NameMatchDispatchResult result = await NameMatchPipeline.DispatchAsync(
            solution, projects, ".ctor", null,
            circlePath,
            Path.GetFullPath(circlePath),
            true, TestContext.Current.CancellationToken);

        // Assert
        result.IsSpecialMember.ShouldBeTrue();
        result.ResolvedContainingType.ShouldBe("Circle");
        result.Candidates.ShouldAllBe(s => s.ContainingType!.Name == "Circle");
    }

    [Fact]
    public async Task Dispatch_SpecialMember_NoContainingTypeNoFile_WithInference_Throws()
    {
        // Arrange — useFileInference=true with no containingType and no filePath surfaces the
        // existing "containingType is required" error from SpecialMemberResolver.
        (Solution solution, IReadOnlyList<Project> projects) = await GetSolutionAndProjectsAsync();

        // Act + Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() =>
            NameMatchPipeline.DispatchAsync(
                solution, projects, ".ctor", null,
                null, null,
                true, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("containingType is required");
    }

    [Fact]
    public async Task Dispatch_SpecialMember_NoContainingTypeNoFile_WithoutInference_WalksAllTypes()
    {
        // Arrange — useFileInference=false (find_symbol exploration mode) walks all source
        // types and returns every constructor in the solution.
        (Solution solution, IReadOnlyList<Project> projects) = await GetSolutionAndProjectsAsync();

        // Act
        NameMatchDispatchResult result = await NameMatchPipeline.DispatchAsync(
            solution, projects, ".ctor", null,
            null, null,
            false, TestContext.Current.CancellationToken);

        // Assert
        result.IsSpecialMember.ShouldBeTrue();
        result.Candidates.ShouldAllBe(s => (s as IMethodSymbol)!.MethodKind == MethodKind.Constructor);
        // Many distinct containing types should be present (Circle, Rectangle, Triangle, etc.)
        result.Candidates.Select(s => s.ContainingType!.Name).Distinct().Count().ShouldBeGreaterThan(1);
    }

    [Fact]
    public async Task Dispatch_OpenGenericSyntax_ExtractsArity()
    {
        // Arrange
        (Solution solution, IReadOnlyList<Project> projects) = await GetSolutionAndProjectsAsync();

        // Act
        NameMatchDispatchResult result = await NameMatchPipeline.DispatchAsync(
            solution, projects, "Processor<>", null,
            null, null,
            false, TestContext.Current.CancellationToken);

        // Assert
        result.SearchName.ShouldBe("Processor");
        result.RequestedArity.ShouldBe(1);
    }

    [Fact]
    public async Task Dispatch_GenericSyntaxConcrete_ThrowsViaFqnParser()
    {
        // Arrange — concrete generic syntax like "Processor<string>" is rejected by FqnParser
        // because arity extraction can't strip it and ThrowIfInvalid fires the generic-syntax guard.
        (Solution solution, IReadOnlyList<Project> projects) = await GetSolutionAndProjectsAsync();

        // Act + Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() =>
            NameMatchPipeline.DispatchAsync(
                solution, projects, "Processor<string>", null,
                null, null,
                false, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("Generic FQN syntax");
    }

    [Fact]
    public async Task Dispatch_FqnWithContainingType_ThrowsViaFqnParser()
    {
        // Arrange — combining an FQN with containingType is rejected by FqnParser.
        (Solution solution, IReadOnlyList<Project> projects) = await GetSolutionAndProjectsAsync();

        // Act + Assert
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() =>
            NameMatchPipeline.DispatchAsync(
                solution, projects, "A.B.C", "X",
                null, null,
                false, TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("fully-qualified name");
        ex.Message.ShouldContain("Do not combine with containingType");
    }

    private async Task<(Solution Solution, IReadOnlyList<Project> Projects)> GetSolutionAndProjectsAsync()
    {
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync();
        return (solution, solution.Projects.ToList());
    }
}
