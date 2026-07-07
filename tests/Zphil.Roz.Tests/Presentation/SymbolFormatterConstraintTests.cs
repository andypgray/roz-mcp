using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Presentation;

public class SymbolFormatterConstraintTests(WorkspaceFixture fixture)
{
    private readonly NavigationTools tools = TestFileHelper.CreateNavigationTools(fixture);

    [Theory]
    [InlineData("UnmanagedProcessor", "unmanaged")]
    [InlineData("StructProcessor", "struct")]
    [InlineData("NotNullProcessor", "notnull")]
    [InlineData("Repository", "class, new()")]
    public async Task FindSymbol_GenericConstraint_ShowsConstraintKeyword(
        string typeName, string expectedConstraint)
    {
        // Act
        string result = await tools.FindSymbol([typeName], matchMode: SymbolMatchMode.Exact, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain($"where T : {expectedConstraint}");
    }

    [Fact]
    public async Task FindSymbol_AsyncMethod_ShowsAsyncTrue()
    {
        // Act
        string result = await tools.FindSymbol(["CalculateAsync"], containingType: "AsyncService", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Async: true");
    }

    [Theory]
    [InlineData("ReadOnly", "Accessors: get")]
    [InlineData("WriteOnly", "Accessors: set")]
    [InlineData("InitOnly", "Accessors: get, init")]
    [InlineData("GetSet", "Accessors: get, set")]
    public async Task FindSymbol_PropertyAccessors_ShowsAccessorKind(
        string propertyName, string expectedAccessors)
    {
        // Act
        string result = await tools.FindSymbol([propertyName], containingType: "PropertyAccessorExamples", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain(expectedAccessors);
    }
}
