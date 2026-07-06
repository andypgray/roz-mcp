using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools;

/// <summary>
///     Tests that the <c>kind</c> parameter disambiguates symbols that share the same name
///     across different symbol categories (e.g. a class and an interface both named "Metric").
/// </summary>
public class KindDisambiguationTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools navTools = TestFileHelper.CreateNavigationTools(fixture);
    private readonly ReferenceTools refTools = TestFileHelper.CreateReferenceTools(fixture);
    private readonly TypeHierarchyTools typeTools = TestFileHelper.CreateTypeTools(fixture);

    // ── find_references ──────────────────────────────────────────────────

    [Fact]
    public async Task FindReferences_KindInterface_ResolvesInterfaceNotClass()
    {
        // Act — "Metric" matches both an interface and a generic class; kind: Interface picks the interface
        string result = await refTools.FindReferences(symbolNames: ["Metric"], kind: SymbolicKind.Interface, ct: TestContext.Current.CancellationToken);

        // Assert — resolved the interface, used by MetricConsumer.CurrentMetric (a `Metric?` property).
        // The generic class Metric<T> is not referenced there, so "CurrentMetric" distinguishes them.
        result.ShouldContain("CurrentMetric");
    }

    [Fact]
    public async Task FindReferences_KindClass_ResolvesGenericClassNotInterface()
    {
        // Act — kind: Class picks Metric<T> (the only class named "Metric")
        string result = await refTools.FindReferences(symbolNames: ["Metric"], kind: SymbolicKind.Class, ct: TestContext.Current.CancellationToken);

        // Assert — kind: Class resolved the (unreferenced) generic class, not the interface. The
        // interface IS referenced (CurrentMetric), so resolving it would show references, not this message.
        result.ShouldContain("No references found");
    }

    // ── find_references referenceKinds=invocations ────────────────────────────────

    [Fact]
    public async Task FindReferences_Invocations_KindMethod_ResolvesMethodNotType()
    {
        // Act — "Measure" is both a method on MetricConsumer; kind: Method targets the method
        string result = await refTools.FindReferences(symbolNames: ["Measure"], containingType: "MetricConsumer", kind: SymbolicKind.Method, referenceKinds: ReferenceKind.Invocations, ct: TestContext.Current.CancellationToken);

        // Assert — should resolve without ambiguity error (there are two Measure overloads, both methods)
        result.ShouldContain("Measure");
    }

    // ── find_implementations ─────────────────────────────────────────────

    [Fact]
    public async Task FindImplementations_KindInterface_FindsImplementorsOfInterface()
    {
        // Act — kind: Interface ensures we get the Metric interface, not Metric<T> class
        string result = await refTools.FindImplementations(symbolNames: ["Metric"], kind: SymbolicKind.Interface, ct: TestContext.Current.CancellationToken);

        // Assert — Metric<T> implements Metric interface
        result.ShouldContain("Metric<T>");
    }

    // ── get_type_hierarchy ───────────────────────────────────────────────

    [Fact]
    public async Task GetTypeHierarchy_KindInterface_GetsInterfaceHierarchy()
    {
        // Act
        string result = await typeTools.GetTypeHierarchy(symbolNames: ["Metric"], kind: SymbolicKind.Interface, ct: TestContext.Current.CancellationToken);

        // Assert — resolved the interface Metric, which implements no interface of its own. (The class
        // Metric<T> would list Metric under "Implemented interfaces"; the interface shows "(none)".)
        string normalized = result.Replace("\r\n", "\n");
        normalized.ShouldContain("Implemented interfaces:\n  (none)");
    }

    [Fact]
    public async Task GetTypeHierarchy_KindClass_GetsClassHierarchy()
    {
        // Act
        string result = await typeTools.GetTypeHierarchy(symbolNames: ["Metric"], kind: SymbolicKind.Class, ct: TestContext.Current.CancellationToken);

        // Assert — resolved the generic class Metric<T>, which implements the Metric interface (unlike
        // the interface Metric, whose "Implemented interfaces" section is "(none)").
        string normalized = result.Replace("\r\n", "\n");
        normalized.ShouldContain("Implemented interfaces:");
        normalized.ShouldNotContain("Implemented interfaces:\n  (none)");
    }

    // ── find_implementations on a type (derived-types dispatch) ──────────────

    [Fact]
    public async Task FindImplementations_OnType_KindInterface_FindsImplementors()
    {
        // Act
        string result = await refTools.FindImplementations(symbolNames: ["Metric"], kind: SymbolicKind.Interface, ct: TestContext.Current.CancellationToken);

        // Assert — Metric<T> implements the Metric interface
        result.ShouldContain("Metric<T>");
    }

    // ── find_symbol with kind (already existed, but verify it still works) ──

    [Fact]
    public async Task FindSymbol_KindInterface_ReturnsOnlyInterface()
    {
        // Act
        string result = await navTools.FindSymbol(["Metric"], SymbolicKind.Interface, ct: TestContext.Current.CancellationToken);

        // Assert — should find only the interface, not the generic class
        result.ShouldContain("interface");
        result.ShouldNotContain("class");
    }

    [Fact]
    public async Task FindSymbol_KindClass_ReturnsOnlyClass()
    {
        // Act
        string result = await navTools.FindSymbol(["Metric"], SymbolicKind.Class, ct: TestContext.Current.CancellationToken);

        // Assert — should find only the class
        result.ShouldContain("class");
        result.ShouldNotContain("interface");
    }

    // ── kind with no match produces clear error ──────────────────────────

    [Fact]
    public async Task FindReferences_KindEnum_NoMatchReturnsDescriptiveError()
    {
        // Act — "Metric" is not an enum; per-name error is captured inline
        string result = await refTools.FindReferences(symbolNames: ["Metric"], kind: SymbolicKind.Enum, ct: TestContext.Current.CancellationToken);

        // KindFilterBlame hint lists existing kinds in PascalCase
        result.ShouldContain("exists as");
        result.ShouldContain("drop the kind filter");
    }

    [Fact]
    public async Task FindImplementations_KindEnum_NoMatchUsesUnifiedHintFormat()
    {
        // Act — "Metric" is not an enum
        string result = await refTools.FindImplementations(symbolNames: ["Metric"], kind: SymbolicKind.Enum, ct: TestContext.Current.CancellationToken);

        // Verify the unified KindFilterBlame format flows through find_implementations too
        result.ShouldContain("\"Metric\" exists as");
        result.ShouldContain("drop the kind filter or use a different kind");
    }
}
