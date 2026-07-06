using Microsoft.Extensions.Logging.Abstractions;
using Zphil.Roz.Infrastructure;
using Zphil.Roz.Services;
using Zphil.Roz.Symbols;
using Zphil.Roz.Tools;

namespace Zphil.Roz.StressTests.Fixtures;

/// <summary>
///     Well-known file paths, tool factories, and test utilities for nopCommerce stress tests.
///     Note: SolutionDirectory is the src/ folder (where NopCommerce.sln lives),
///     so paths are relative to that — no leading "src/" prefix.
/// </summary>
internal static class NopTestFileHelper
{
    /// <summary>
    ///     Composes a <c>location</c> string for tool calls (e.g. <c>"Foo.cs:42:18"</c>).
    /// </summary>
    internal static string Loc(string filePath, int? line = null, int? column = null) =>
        LocationFormat.Format(filePath, line, column);

    // ── Tool factories ────────────────────────────────────────────────────

    internal static CodeEditTools CreateEditTools(ITestWorkspace ws)
    {
        var resolution = new EditSymbolResolver(ws.WorkspaceManager);
        var verification = new EditVerificationService(ws.WorkspaceManager);
        return new CodeEditTools(
            new SymbolEditService(ws.WorkspaceManager, ws.BaselineManager, resolution, verification, NullLogger<SymbolEditService>.Instance),
            new RenameService(ws.WorkspaceManager, ws.BaselineManager, resolution, verification),
            new TextReplacementService(ws.WorkspaceManager, ws.BaselineManager, verification),
            new CodeFixService(ws.WorkspaceManager, ws.BaselineManager,
                new FixerCatalog(ws.WorkspaceManager, NullLogger<FixerCatalog>.Instance), verification),
            new ChangeSignatureService(ws.WorkspaceManager, resolution, ws.BaselineManager, verification));
    }

    internal static NavigationTools CreateNavigationTools(ITestWorkspace ws) =>
        new(new NavigationService(ws.WorkspaceManager, new SymbolResolver(ws.WorkspaceManager)),
            new MethodAnalysisService(
                new SymbolResolver(ws.WorkspaceManager),
                new ReferenceService(new SymbolResolver(ws.WorkspaceManager), new DiRegistrationScanner()),
                new NavigationService(ws.WorkspaceManager, new SymbolResolver(ws.WorkspaceManager))));

    internal static ReferenceTools CreateReferenceTools(ITestWorkspace ws) =>
        new(new ReferenceService(new SymbolResolver(ws.WorkspaceManager), new DiRegistrationScanner()),
            new ImpactAnalysisService(new SymbolResolver(ws.WorkspaceManager)));

    internal static TypeHierarchyTools CreateTypeTools(ITestWorkspace ws) =>
        new(new TypeHierarchyService(new SymbolResolver(ws.WorkspaceManager)));

    internal static DiagnosticTools CreateDiagnosticTools(ITestWorkspace ws) =>
        new(new DiagnosticService(ws.WorkspaceManager, ws.BaselineManager,
            new FixerCatalog(ws.WorkspaceManager, NullLogger<FixerCatalog>.Instance)), ws.BaselineManager);

    internal static UsingDirectiveTools CreateUsingTools(ITestWorkspace ws) =>
        new(new UsingDirectiveService(ws.WorkspaceManager, ws.BaselineManager));

    internal static WorkspaceTools CreateWorkspaceTools(ITestWorkspace ws) =>
        new(new WorkspaceService(ws.WorkspaceManager), new UnusedReferenceService(ws.WorkspaceManager));

    // ── File path helpers ─────────────────────────────────────────────────

    internal static string ProductFile(WorkspaceManager ws) =>
        Path.Combine(ws.SolutionDirectory!, "Libraries", "Nop.Core", "Domain", "Catalog", "Product.cs");

    internal static string CustomerFile(WorkspaceManager ws) =>
        Path.Combine(ws.SolutionDirectory!, "Libraries", "Nop.Core", "Domain", "Customers", "Customer.cs");

    internal static string BaseEntityFile(WorkspaceManager ws) =>
        Path.Combine(ws.SolutionDirectory!, "Libraries", "Nop.Core", "BaseEntity.cs");

    internal static string ProductServiceFile(WorkspaceManager ws) =>
        Path.Combine(ws.SolutionDirectory!, "Libraries", "Nop.Services", "Catalog", "ProductService.cs");

    internal static string CategoryServiceFile(WorkspaceManager ws) =>
        Path.Combine(ws.SolutionDirectory!, "Libraries", "Nop.Services", "Catalog", "CategoryService.cs");

    /// <summary>
    ///     A <c>Nop.Core.Domain.&lt;area&gt;.&lt;typeName&gt;</c> entity file — most are
    ///     single-type <c>BaseEntity</c> subtypes, so batching cursors across several of them
    ///     stresses multi-location resolution without a helper per entity.
    /// </summary>
    internal static string CatalogEntityFile(WorkspaceManager ws, string typeName) =>
        Path.Combine(ws.SolutionDirectory!, "Libraries", "Nop.Core", "Domain", "Catalog", $"{typeName}.cs");

    internal static string IRepositoryFile(WorkspaceManager ws) =>
        Path.Combine(ws.SolutionDirectory!, "Libraries", "Nop.Data", "IRepository.cs");

    internal static string OrderFile(WorkspaceManager ws) =>
        Path.Combine(ws.SolutionDirectory!, "Libraries", "Nop.Core", "Domain", "Orders", "Order.cs");

    internal static string ProductControllerFile(WorkspaceManager ws) =>
        Path.Combine(ws.SolutionDirectory!, "Presentation", "Nop.Web", "Areas", "Admin", "Controllers", "ProductController.cs");

    internal static string AffiliateServiceInterfaceFile(WorkspaceManager ws) =>
        Path.Combine(ws.SolutionDirectory!, "Libraries", "Nop.Services", "Affiliates", "IAffiliateService.cs");

    // ── Utilities ─────────────────────────────────────────────────────────

    internal static string[] SplitLines(string content) => content.Replace("\r\n", "\n").Split('\n');

    internal static void AssertConsistentLineEndings(string content)
    {
        bool hasCrlf = content.Contains("\r\n");
        bool hasBareLf = content.Replace("\r\n", "").Contains('\n');
        (hasCrlf && hasBareLf).ShouldBeFalse("File should not have mixed line endings");
    }

    internal static async Task RewriteFileAsync(ITestWorkspace ws, string filePath, Func<string, string> transform)
    {
        string content = await File.ReadAllTextAsync(filePath);
        await File.WriteAllTextAsync(filePath, transform(content));
        await ws.WorkspaceManager.ReloadAsync();
    }
}
