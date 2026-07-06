using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

public class LanguageVersionGuardTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task ReplaceSymbol_RecordInLegacyProject_ThrowsWithVersionMessage()
    {
        // Arrange — TestFixture.Legacy is C# 7.3; records require C# 9
        CodeEditTools tools = CreateEditTools(Fixture);
        string legacyFile = LegacyClassFile(Fixture);
        const string recordDeclaration = "public record Point(double X, double Y);";

        // Act & Assert
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.ReplaceSymbol(legacyFile, "DoWork", recordDeclaration));

        ex.Message.ShouldContain("Language version conflict");
        ex.Message.ShouldContain("7.3");
    }

    [Fact]
    public async Task InsertSymbol_After_RecordInLegacyProject_ThrowsWithVersionMessage()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string legacyFile = LegacyClassFile(Fixture);
        const string recordDeclaration = "public record Point(double X, double Y);";

        // Act & Assert
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.InsertSymbol(legacyFile, "DoWork", recordDeclaration));

        ex.Message.ShouldContain("Language version conflict");
        ex.Message.ShouldContain("7.3");
    }

    [Fact]
    public async Task ReplaceSymbol_ValidCodeInLegacyProject_Succeeds()
    {
        // Arrange — plain method is valid in C# 7.3
        CodeEditTools tools = CreateEditTools(Fixture);
        string legacyFile = LegacyClassFile(Fixture);
        const string newMethod = "public void DoWork() { Console.WriteLine(\"Updated\"); }";

        // Act
        string result = await tools.ReplaceSymbol(legacyFile, "DoWork", newMethod, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("DoWork");
        string fileContent = await File.ReadAllTextAsync(legacyFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("Updated");
    }

    [Fact]
    public async Task InsertSymbol_Before_RecordInLegacyProject_ThrowsWithVersionMessage()
    {
        // Arrange — InsertBefore path was previously untested for the guard
        CodeEditTools tools = CreateEditTools(Fixture);
        string legacyFile = LegacyClassFile(Fixture);
        const string recordDeclaration = "public record Point(double X, double Y);";

        // Act & Assert
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.InsertSymbol(legacyFile, "DoWork", recordDeclaration, InsertPosition.Before));

        ex.Message.ShouldContain("Language version conflict");
        ex.Message.ShouldContain("7.3");
    }

    [Fact]
    public async Task InsertSymbol_Before_MethodInLegacyProject_Succeeds()
    {
        // Arrange — valid C# 7.3 method through the InsertBefore pipeline
        CodeEditTools tools = CreateEditTools(Fixture);
        string legacyFile = LegacyClassFile(Fixture);
        const string newMethod = "public int GetCount() { return 0; }";

        // Act
        string result = await tools.InsertSymbol(legacyFile, "DoWork", newMethod, InsertPosition.Before, ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("DoWork");
        string fileContent = await File.ReadAllTextAsync(legacyFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("GetCount");
    }

    [Fact]
    public async Task InsertSymbol_After_FieldInLegacyProject_Succeeds()
    {
        // Arrange — field insertion in C# 7.3 project
        CodeEditTools tools = CreateEditTools(Fixture);
        string legacyFile = LegacyClassFile(Fixture);
        const string field = "private int _count;";

        // Act
        await tools.InsertSymbol(legacyFile, "Name", field, ct: TestContext.Current.CancellationToken);

        // Assert
        string fileContent = await File.ReadAllTextAsync(legacyFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("_count");
    }

    [Fact]
    public async Task InsertSymbol_After_EventInLegacyProject_Succeeds()
    {
        // Arrange — event insertion in C# 7.3 project
        CodeEditTools tools = CreateEditTools(Fixture);
        string legacyFile = LegacyClassFile(Fixture);
        const string eventDecl = "public event EventHandler Changed;";

        // Act
        await tools.InsertSymbol(legacyFile, "DoWork", eventDecl, ct: TestContext.Current.CancellationToken);

        // Assert
        string fileContent = await File.ReadAllTextAsync(legacyFile, TestContext.Current.CancellationToken);
        fileContent.ShouldContain("Changed");
    }

    [Fact]
    public async Task ReplaceSymbol_RecordStructInLegacyProject_ThrowsWithDiagnostics()
    {
        // Arrange — record struct requires C# 10; in C# 7.3 it fails to parse entirely
        // (targetVersionResult is null), exercising the ExtractDiagnosticsFromReparse path
        CodeEditTools tools = CreateEditTools(Fixture);
        string legacyFile = LegacyClassFile(Fixture);
        const string recordStructDecl = "public record struct Point(double X, double Y);";

        // Act & Assert
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.ReplaceSymbol(legacyFile, "DoWork", recordStructDecl));

        ex.Message.ShouldContain("not available");
        ex.Message.ShouldContain("7.3");
    }

    [Fact]
    public async Task InsertSymbol_After_RecordStructInLegacyProject_ThrowsWithDiagnostics()
    {
        // Arrange — record struct in C# 7.3 fails to parse (null from TryParseMember),
        // exercising the targetVersionResult-is-null path in InsertRelativeToSymbolAsync
        CodeEditTools tools = CreateEditTools(Fixture);
        string legacyFile = LegacyClassFile(Fixture);
        const string recordStructDecl = "public record struct Point(double X, double Y);";

        // Act & Assert
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.InsertSymbol(legacyFile, "DoWork", recordStructDecl));

        ex.Message.ShouldContain("not available");
        ex.Message.ShouldContain("7.3");
    }
}
