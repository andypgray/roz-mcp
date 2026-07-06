using Microsoft.CodeAnalysis;
using Zphil.Roz.Models;
using Zphil.Roz.Services;
using Zphil.Roz.Tests.Fixtures;

namespace Zphil.Roz.Tests.Services;

public class DiRegistrationScannerTests(WorkspaceFixture fixture)
{
    private readonly DiRegistrationScanner scanner = new();

    private static async Task<INamedTypeSymbol> GetTypeSymbolAsync(
        Solution solution, string typeName, string fileName, CancellationToken ct)
    {
        List<Document> matchingDocs = solution.Projects
            .SelectMany(p => p.Documents)
            .Where(d => d.FilePath?.EndsWith(fileName) == true)
            .ToList();

        foreach (Document doc in matchingDocs)
        {
            SemanticModel? model = await doc.GetSemanticModelAsync(ct);
            INamedTypeSymbol? symbol = model?.Compilation
                .GetSymbolsWithName(typeName, SymbolFilter.Type)
                .OfType<INamedTypeSymbol>()
                .FirstOrDefault();

            if (symbol is not null)
            {
                return symbol;
            }
        }

        throw new InvalidOperationException($"{typeName} type not found in solution");
    }

    [Theory]
    [InlineData("Circle", "Circle.cs", "Autofac", "Scoped")]
    [InlineData("ShapeService", "ShapeService.cs", "Autofac", "Singleton")]
    [InlineData("Circle", "Circle.cs", "Ninject", "Thread")]
    [InlineData("ShapeService", "ShapeService.cs", "Ninject", "Singleton")]
    [InlineData("Circle", "Circle.cs", "Unity", "Singleton")]
    [InlineData("ShapeCalculator", "ShapeCalculator.cs", "Unity", "Scoped")]
    [InlineData("Circle", "Circle.cs", "SimpleInjector", "Singleton")]
    [InlineData("ShapeCalculator", "ShapeCalculator.cs", "SimpleInjector", "Scoped")]
    [InlineData("Circle", "Circle.cs", "DryIoc", "Singleton")]
    [InlineData("ShapeCalculator", "ShapeCalculator.cs", "DryIoc", "Scoped")]
    [InlineData("Circle", "Circle.cs", "Lamar", "Singleton")]
    [InlineData("ShapeService", "ShapeService.cs", "Lamar", "Scoped")]
    [InlineData("Circle", "Circle.cs", "Windsor", "Singleton")]
    [InlineData("ShapeService", "ShapeService.cs", "Windsor", "Transient")]
    public async Task FindRegistrations_ForType_DetectsContainerWithLifetime(
        string typeName, string fileName, string containerName, string expectedLifetime)
    {
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        INamedTypeSymbol type = await GetTypeSymbolAsync(solution, typeName, fileName, TestContext.Current.CancellationToken);

        IReadOnlyList<DiRegistration> registrations = await scanner.FindRegistrationsAsync(type, solution, TestContext.Current.CancellationToken);

        registrations.ShouldContain(r => r.ContainerName == containerName && r.Lifetime == expectedLifetime);
    }

    [Fact]
    public async Task FindRegistrations_Circle_FindsAllContainers()
    {
        // Arrange — Circle is registered in all 8 containers
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        INamedTypeSymbol circle = await GetTypeSymbolAsync(solution, "Circle", "Circle.cs", TestContext.Current.CancellationToken);

        // Act
        IReadOnlyList<DiRegistration> registrations = await scanner.FindRegistrationsAsync(circle, solution, TestContext.Current.CancellationToken);

        // Assert — verify all container names are present
        string[] containerNames = registrations.Select(r => r.ContainerName).Distinct().OrderBy(n => n).ToArray();
        containerNames.ShouldContain("Autofac");
        containerNames.ShouldContain("DryIoc");
        containerNames.ShouldContain("Lamar");
        containerNames.ShouldContain("MEDI");
        containerNames.ShouldContain("Ninject");
        containerNames.ShouldContain("SimpleInjector");
        containerNames.ShouldContain("Unity");
        containerNames.ShouldContain("Windsor");
    }

    [Fact]
    public async Task FindRegistrations_Circle_MediRegistrationStillWorks()
    {
        // Arrange — ensure the MEDI recognizer still works after refactoring
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        INamedTypeSymbol circle = await GetTypeSymbolAsync(solution, "Circle", "Circle.cs", TestContext.Current.CancellationToken);

        // Act
        IReadOnlyList<DiRegistration> registrations = await scanner.FindRegistrationsAsync(circle, solution, TestContext.Current.CancellationToken);

        // Assert
        registrations.ShouldContain(r =>
            r.ContainerName == "MEDI"
            && r.Lifetime == "Scoped"
            && r.FilePath.EndsWith("ServiceRegistration.cs"));
    }

    // ── Nested builder/configurator lambdas ─────────────────────────────────

    [Fact]
    public async Task FindRegistrations_Triangle_FindsNestedBuilderRegistration()
    {
        // Arrange — Triangle is registered via services.AddShapes(s => s.AddShape<Triangle>()).
        // The inner AddShape<Triangle> invocation targets IShapeBuilder, so no recognizer
        // matches it directly; detection relies on walking out to AddShapes (MEDI).
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        INamedTypeSymbol triangle = await GetTypeSymbolAsync(solution, "Triangle", "Triangle.cs", TestContext.Current.CancellationToken);

        // Act
        IReadOnlyList<DiRegistration> registrations = await scanner.FindRegistrationsAsync(triangle, solution, TestContext.Current.CancellationToken);

        // Assert
        registrations.ShouldContain(r =>
            r.ContainerName == "MEDI"
            && r.Lifetime == "Scoped"
            && r.FilePath.EndsWith("DemoRegistration.cs")
            && r.LineText.Contains("AddShape<Triangle>"));
    }

    [Fact]
    public async Task FindRegistrations_Rectangle_FindsMethodChainBuilderRegistration()
    {
        // Arrange — Rectangle is registered via services.UseTracing().WithTracing(t => t.AddSource<Rectangle>()).
        // WithTracing's first param is ITracingBuilder so MEDI doesn't match it; detection
        // walks the fluent chain backward to UseTracing (IServiceCollection → MEDI matches).
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        INamedTypeSymbol rectangle = await GetTypeSymbolAsync(solution, "Rectangle", "Rectangle.cs", TestContext.Current.CancellationToken);

        // Act
        IReadOnlyList<DiRegistration> registrations = await scanner.FindRegistrationsAsync(rectangle, solution, TestContext.Current.CancellationToken);

        // Assert
        registrations.ShouldContain(r =>
            r.ContainerName == "MEDI"
            && r.Lifetime == "Scoped"
            && r.FilePath.EndsWith("DemoRegistration.cs")
            && r.LineText.Contains("AddSource<Rectangle>"));
    }

    [Fact]
    public async Task FindRegistrations_ConfigureShape_IsIgnored()
    {
        // Arrange — DemoRegistration.cs contains services.AddShapes(s => s.ConfigureShape<Triangle>()),
        // which should NOT be detected as a registration because the inner method name
        // doesn't start with "Add" (validates the Add* prefix filter).
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        INamedTypeSymbol triangle = await GetTypeSymbolAsync(solution, "Triangle", "Triangle.cs", TestContext.Current.CancellationToken);

        // Act
        IReadOnlyList<DiRegistration> registrations = await scanner.FindRegistrationsAsync(triangle, solution, TestContext.Current.CancellationToken);

        // Assert — no registration should be reported on a ConfigureShape line
        registrations.ShouldNotContain(r => r.LineText.Contains("ConfigureShape"));
    }

    // ── MEDI specifics (lifetime + filename) ────────────────────────────────

    [Fact]
    public async Task FindRegistrations_ShapeService_FindsTransientRegistration()
    {
        // Arrange
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        INamedTypeSymbol shapeService = await GetTypeSymbolAsync(solution, "ShapeService", "ShapeService.cs", TestContext.Current.CancellationToken);

        // Act
        IReadOnlyList<DiRegistration> registrations = await scanner.FindRegistrationsAsync(shapeService, solution, TestContext.Current.CancellationToken);

        // Assert
        registrations.Count.ShouldBeGreaterThan(0, "Expected at least one DI registration for ShapeService");
        // && the predicates: ShapeService is Transient in both ServiceRegistration.cs and
        // WindsorRegistration.cs, so two independent ShouldContains could match different registrations.
        registrations.ShouldContain(r => r.Lifetime == "Transient" && r.FilePath.EndsWith("ServiceRegistration.cs"));
    }

    [Fact]
    public async Task FindRegistrations_Circle_FindsScopedRegistration()
    {
        // Arrange
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        INamedTypeSymbol circle = await GetTypeSymbolAsync(solution, "Circle", "Circle.cs", TestContext.Current.CancellationToken);

        // Act
        IReadOnlyList<DiRegistration> registrations = await scanner.FindRegistrationsAsync(circle, solution, TestContext.Current.CancellationToken);

        // Assert
        registrations.Count.ShouldBeGreaterThan(0, "Expected at least one DI registration for Circle");
        registrations.ShouldContain(r => r.Lifetime == "Scoped");
    }

    [Fact]
    public async Task FindRegistrations_TypeWithNoDi_ReturnsEmpty()
    {
        // Arrange — ShapeHelper has no DI registration
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        INamedTypeSymbol shapeHelper = await GetTypeSymbolAsync(solution, "ShapeHelper", "ShapeHelper.cs", TestContext.Current.CancellationToken);

        // Act
        IReadOnlyList<DiRegistration> registrations = await scanner.FindRegistrationsAsync(shapeHelper, solution, TestContext.Current.CancellationToken);

        // Assert
        registrations.Count.ShouldBe(0);
    }

    // ── CR-7: MEDI default lifetime ─────────────────────────────────────────

    [Fact]
    public async Task FindRegistrations_MediAddSingleton_StaysSingleton()
    {
        // Arrange — services.AddSingleton<ShapeCalculator>() in ServiceRegistration.cs.
        // Regression guard for CR-7: flipping the MEDI catch-all to Transient must not regress
        // AddSingleton — the explicit Singleton branch added before the catch-all keeps it Singleton.
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        INamedTypeSymbol shapeCalculator = await GetTypeSymbolAsync(solution, "ShapeCalculator", "ShapeCalculator.cs", TestContext.Current.CancellationToken);

        // Act
        IReadOnlyList<DiRegistration> registrations = await scanner.FindRegistrationsAsync(shapeCalculator, solution, TestContext.Current.CancellationToken);

        // Assert
        registrations.ShouldContain(r =>
            r.ContainerName == "MEDI"
            && r.Lifetime == "Singleton"
            && r.FilePath.EndsWith("ServiceRegistration.cs"));
    }

    // ── CR-11: namespace-prefix boundary matching ───────────────────────────

    [Fact]
    public async Task FindRegistrations_CastleDynamicProxyCall_NotReportedAsWindsor()
    {
        // Arrange — CastleProxyTarget is only referenced by a Castle.DynamicProxy proxy-creation
        // call. CR-11a: "Castle.DynamicProxy" shares the bare "Castle" prefix the old Windsor
        // recognizer matched, but is not a DI registration. Dotted-boundary matching excludes it.
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        INamedTypeSymbol target = await GetTypeSymbolAsync(solution, "CastleProxyTarget", "CastleProxyRegistration.cs", TestContext.Current.CancellationToken);

        // Act
        IReadOnlyList<DiRegistration> registrations = await scanner.FindRegistrationsAsync(target, solution, TestContext.Current.CancellationToken);

        // Assert — the old code reported this as Windsor/Transient
        registrations.ShouldNotContain(r => r.ContainerName == "Windsor");
    }

    [Fact]
    public async Task FindRegistrations_UserNinjectHelpersNamespace_NotReportedAsNinject()
    {
        // Arrange — NinjectHelperTarget is only referenced from a method in the user namespace
        // "NinjectHelpers". CR-11b: that namespace shares the bare "Ninject" prefix the old
        // recognizer matched, but is not Ninject. Dotted-boundary matching excludes it.
        Solution solution = await fixture.WorkspaceManager.GetSolutionAsync(TestContext.Current.CancellationToken);
        INamedTypeSymbol target = await GetTypeSymbolAsync(solution, "NinjectHelperTarget", "NinjectHelpersRegistration.cs", TestContext.Current.CancellationToken);

        // Act
        IReadOnlyList<DiRegistration> registrations = await scanner.FindRegistrationsAsync(target, solution, TestContext.Current.CancellationToken);

        // Assert — the old code reported this as Ninject/Transient
        registrations.ShouldNotContain(r => r.ContainerName == "Ninject");
    }
}
