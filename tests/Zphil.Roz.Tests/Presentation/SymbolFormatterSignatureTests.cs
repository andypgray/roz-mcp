using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Presentation;

public class SymbolFormatterSignatureTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = TestFileHelper.CreateNavigationTools(fixture);

    // ── Primary constructor parameters ────────────────────────────────────

    [Fact]
    public async Task FindSymbol_RecordClass_ShowsPositionalParams()
    {
        string result = await tools.FindSymbol(["ShapeSnapshot"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("record class ShapeSnapshot(string Name, double Area)");
    }

    [Fact]
    public async Task FindSymbol_RecordStruct_ShowsPositionalParams()
    {
        string result = await tools.FindSymbol(["ShapeId"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("record struct ShapeId(int Value)");
    }

    [Fact]
    public async Task FindSymbol_PrimaryCtorStruct_ShowsParams()
    {
        string result = await tools.FindSymbol(["Point"], SymbolicKind.Struct, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("struct Point(double x, double y)");
    }

    [Fact]
    public async Task FindSymbol_PrimaryCtorClass_ShowsParams()
    {
        string result = await tools.FindSymbol(["Circle"], SymbolicKind.Class, matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("class Circle(double radius) : Shape");
    }

    // ── Readonly struct modifier ──────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_ReadonlyRecordStruct_ShowsReadonly()
    {
        string result = await tools.FindSymbol(["ReadonlyShapeId"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("public readonly record struct ReadonlyShapeId");
    }

    // ── [Flags] attribute on enums ────────────────────────────────────────

    [Fact]
    public async Task FindSymbol_FlagsEnum_ShowsFlagsAttribute()
    {
        string result = await tools.FindSymbol(["ShapeFeatures"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("[Flags] public enum ShapeFeatures");
    }

    [Fact]
    public async Task FindSymbol_NonFlagsEnum_DoesNotShowFlagsAttribute()
    {
        string result = await tools.FindSymbol(["ShapeColor"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        result.ShouldNotContain("[Flags]");
    }

    // ── Extension methods (this modifier) ─────────────────────────────────

    [Fact]
    public async Task FindSymbol_ExtensionMethod_ShowsThisModifier()
    {
        string result = await tools.FindSymbol(["AddShapes"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("this IServiceCollection services");
    }

    // ── Optional parameter default values ─────────────────────────────────

    [Fact]
    public async Task FindSymbol_OptionalParam_ShowsDefaultValue()
    {
        string result = await tools.FindSymbol(["Label"], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("string prefix = \"Shape\"");
    }
}
