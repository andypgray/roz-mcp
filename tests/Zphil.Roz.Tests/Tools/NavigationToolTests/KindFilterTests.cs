using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Tools.NavigationToolTests;

public class KindFilterTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = TestFileHelper.CreateNavigationTools(fixture);

    [Theory]
    [InlineData("Point", nameof(SymbolicKind.Struct), "struct")]
    [InlineData("ShapeColor", nameof(SymbolicKind.Enum), "enum")]
    [InlineData("ShapeMetricFunc", nameof(SymbolicKind.Delegate), "delegate")]
    [InlineData("ShapeAdded", nameof(SymbolicKind.Event), "event")]
    [InlineData("_resetCount", nameof(SymbolicKind.Field), "field")]
    public async Task FindSymbol_WithKind_FindsExpectedKind(string symbolName, string kindName, string keyword)
    {
        SymbolicKind kind = Enum.Parse<SymbolicKind>(kindName);

        string result = await tools.FindSymbol([symbolName], kind, ct: TestContext.Current.CancellationToken);

        result.ShouldContain(keyword);
        result.ShouldContain(symbolName);
    }

    [Fact]
    public async Task FindSymbol_WithKindProperty_FindsOnlyProperties()
    {
        string result = await tools.FindSymbol(["Area"], SymbolicKind.Property, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("property");
        result.ShouldNotContain("method");
    }

    [Fact]
    public async Task FindSymbol_WithKindEvent_DoesNotDuplicateEventKeyword()
    {
        string result = await tools.FindSymbol(["ShapeAdded"], SymbolicKind.Event, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("event");
        result.ShouldNotContain("event event");
    }

    [Fact]
    public async Task FindSymbol_Enum_DoesNotShowSealed()
    {
        string result = await tools.FindSymbol(["ShapeColor"], SymbolicKind.Enum, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("enum");
        result.ShouldNotContain("sealed");
    }

    [Fact]
    public async Task FindSymbol_Struct_DoesNotShowSealed()
    {
        string result = await tools.FindSymbol(["Point"], SymbolicKind.Struct, ct: TestContext.Current.CancellationToken);

        result.ShouldContain("struct");
        result.ShouldNotContain("sealed");
    }

    [Fact]
    public async Task FindSymbol_RecordClass_ShowsRecordClass()
    {
        string result = await tools.FindSymbol(["ShapeSnapshot"], ct: TestContext.Current.CancellationToken);

        result.ShouldContain("record class");
    }

    [Fact]
    public async Task FindSymbol_RecordStruct_ShowsRecordStruct()
    {
        string result = await tools.FindSymbol(["ShapeId"], ct: TestContext.Current.CancellationToken);

        result.ShouldContain("record struct");
    }
}
