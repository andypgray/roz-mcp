using Microsoft.CodeAnalysis;
using Zphil.Roz.Symbols;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Symbols;

public class InterfaceImplementationLookupTests(WorkspaceFixture fixture)
{
    [Fact]
    public async Task FindInterfaceMembers_ImplicitImplementation_ReturnsMember()
    {
        // Arrange — Shape.Describe() implicitly implements IShape.Describe()
        INamedTypeSymbol shape = await GetTypeAsync("TestFixture.Shapes.Shape");
        IMethodSymbol describe = shape.GetMembers("Describe").OfType<IMethodSymbol>().Single();

        // Act
        IReadOnlyList<ISymbol> results = InterfaceImplementationLookup.FindInterfaceMembers(describe);

        // Assert
        results.Count.ShouldBe(1);
        results[0].ContainingType.Name.ShouldBe("IShape");
        results[0].Name.ShouldBe("Describe");
    }

    [Fact]
    public async Task FindInterfaceMembers_MethodImplementingTwoInterfaces_ReturnsBoth()
    {
        // Arrange — AlphaBetaHandler.Handle() implements IAlpha.Handle() and IBeta.Handle()
        INamedTypeSymbol handler = await GetTypeAsync("TestFixture.Services.AlphaBetaHandler");
        IMethodSymbol handle = handler.GetMembers("Handle").OfType<IMethodSymbol>().Single();

        // Act
        IReadOnlyList<ISymbol> results = InterfaceImplementationLookup.FindInterfaceMembers(handle);

        // Assert
        results.Count.ShouldBe(2);
        results.Select(r => r.ContainingType.Name).ShouldBe(["IAlpha", "IBeta"], true);
    }

    [Fact]
    public async Task FindInterfaceMembers_ExplicitInterfaceImplementation_ReturnsMember()
    {
        // Arrange — ShapeManager.IResettable.Reset() is an explicit impl
        INamedTypeSymbol manager = await GetTypeAsync("TestFixture.Services.ShapeManager");
        IMethodSymbol reset = manager.GetMembers()
            .OfType<IMethodSymbol>()
            .Single(m => m.ExplicitInterfaceImplementations.Length > 0
                         && m.ExplicitInterfaceImplementations[0].Name == "Reset");

        // Act
        IReadOnlyList<ISymbol> results = InterfaceImplementationLookup.FindInterfaceMembers(reset);

        // Assert
        results.Count.ShouldBe(1);
        results[0].ContainingType.Name.ShouldBe("IResettable");
        results[0].Name.ShouldBe("Reset");
    }

    [Fact]
    public async Task FindInterfaceMembers_ExplicitExternalInterface_ReturnsMetadataMember()
    {
        // Arrange — ShapeManager.IDisposable.Dispose() explicitly implements System.IDisposable.Dispose
        INamedTypeSymbol manager = await GetTypeAsync("TestFixture.Services.ShapeManager");
        IMethodSymbol dispose = manager.GetMembers()
            .OfType<IMethodSymbol>()
            .Single(m => m.ExplicitInterfaceImplementations.Length > 0
                         && m.ExplicitInterfaceImplementations[0].Name == "Dispose");

        // Act
        IReadOnlyList<ISymbol> results = InterfaceImplementationLookup.FindInterfaceMembers(dispose);

        // Assert — metadata symbol: no source location available
        results.Count.ShouldBe(1);
        results[0].ContainingType.Name.ShouldBe("IDisposable");
        results[0].Locations.Any(l => l.IsInSource).ShouldBeFalse();
    }

    [Fact]
    public async Task FindInterfaceMembers_InterfaceMemberItself_ReturnsEmpty()
    {
        // Arrange — IShape.Describe (the interface member itself)
        INamedTypeSymbol ishape = await GetTypeAsync("TestFixture.Shapes.IShape");
        IMethodSymbol describe = ishape.GetMembers("Describe").OfType<IMethodSymbol>().Single();

        // Act
        IReadOnlyList<ISymbol> results = InterfaceImplementationLookup.FindInterfaceMembers(describe);

        // Assert — an interface member doesn't implement itself
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task FindInterfaceMembers_VirtualMethodNoInterface_ReturnsEmpty()
    {
        // Arrange — StandaloneVirtualMethods.DoWork is virtual but on a class with no interfaces
        INamedTypeSymbol type = await GetTypeAsync("TestFixture.Services.StandaloneVirtualMethods");
        IMethodSymbol doWork = type.GetMembers("DoWork").OfType<IMethodSymbol>().Single();

        // Act
        IReadOnlyList<ISymbol> results = InterfaceImplementationLookup.FindInterfaceMembers(doWork);

        // Assert
        results.ShouldBeEmpty();
    }

    [Fact]
    public async Task FindInterfaceMembers_Property_ReturnsInterfaceProperty()
    {
        // Arrange — Shape.Area (abstract property) implements IShape.Area
        INamedTypeSymbol shape = await GetTypeAsync("TestFixture.Shapes.Shape");
        IPropertySymbol area = shape.GetMembers("Area").OfType<IPropertySymbol>().Single();

        // Act
        IReadOnlyList<ISymbol> results = InterfaceImplementationLookup.FindInterfaceMembers(area);

        // Assert
        results.Count.ShouldBe(1);
        results[0].ContainingType.Name.ShouldBe("IShape");
        results[0].Name.ShouldBe("Area");
        results[0].ShouldBeAssignableTo<IPropertySymbol>();
    }

    [Fact]
    public async Task FindInterfaceMember_SingleResult_ReturnsFirstMatch()
    {
        // Arrange — Shape.Describe() implements IShape.Describe()
        INamedTypeSymbol shape = await GetTypeAsync("TestFixture.Shapes.Shape");
        IMethodSymbol describe = shape.GetMembers("Describe").OfType<IMethodSymbol>().Single();

        // Act
        ISymbol? result = InterfaceImplementationLookup.FindInterfaceMember(describe);

        // Assert
        result.ShouldNotBeNull();
        result.ContainingType.Name.ShouldBe("IShape");
    }

    [Fact]
    public async Task FindInterfaceMember_NoMatch_ReturnsNull()
    {
        // Arrange
        INamedTypeSymbol type = await GetTypeAsync("TestFixture.Services.StandaloneVirtualMethods");
        IMethodSymbol doWork = type.GetMembers("DoWork").OfType<IMethodSymbol>().Single();

        // Act
        ISymbol? result = InterfaceImplementationLookup.FindInterfaceMember(doWork);

        // Assert
        result.ShouldBeNull();
    }

    private async Task<INamedTypeSymbol> GetTypeAsync(string metadataName)
    {
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync();
        Project project = solution.Projects.Single(p => p.Name == "TestFixture");
        Compilation compilation = (await project.GetCompilationAsync())!;
        INamedTypeSymbol? type = compilation.GetTypeByMetadataName(metadataName);
        type.ShouldNotBeNull($"Type '{metadataName}' not found in TestFixture project.");
        return type;
    }
}
