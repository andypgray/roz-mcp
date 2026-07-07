using Zphil.Roz.Enums;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Tests that edit tools respect the containingType parameter when multiple
///     types in the same file share identically-named members.
/// </summary>
public class ContainingTypeDisambiguationTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task ReplaceSymbol_WithContainingType_TargetsCorrectType()
    {
        // Arrange — Animals.cs has Dog.Speak ("Woof") and Cat.Speak ("Meow")
        CodeEditTools tools = CreateEditTools(Fixture);
        string animalsFile = AnimalsFile(Fixture);
        var newDeclaration = """public string Speak() => "Hiss";""";

        // Act — replace Cat.Speak only
        string result = await tools.ReplaceSymbol(animalsFile, "Speak", newDeclaration, containingType: "Cat", ct: TestContext.Current.CancellationToken);

        // Assert — Cat.Speak replaced, Dog.Speak untouched
        result.ShouldContain("Speak");
        string content = await File.ReadAllTextAsync(animalsFile, TestContext.Current.CancellationToken);
        content.ShouldContain("\"Hiss\"");
        content.ShouldContain("\"Woof\"");
        content.ShouldNotContain("\"Meow\"");
    }

    [Fact]
    public async Task ReplaceSymbol_WithContainingType_NoMatch_Throws()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string animalsFile = AnimalsFile(Fixture);

        // Act & Assert — "Fish" doesn't exist in the file
        InvalidOperationException ex = await Should.ThrowAsync<InvalidOperationException>(() => tools.ReplaceSymbol(animalsFile, "Speak", "public string Speak() => \"Blub\";", containingType: "Fish"));

        ex.Message.ShouldContain("not found");
    }

    [Fact]
    public async Task RemoveSymbol_WithContainingType_TargetsCorrectType()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string animalsFile = AnimalsFile(Fixture);

        // Act — remove Cat.Speak only
        string result = await tools.RemoveSymbol(animalsFile, "Speak", containingType: "Cat", ct: TestContext.Current.CancellationToken);

        // Assert — Cat.Speak removed, Dog.Speak remains
        result.ShouldContain("Removed");
        string content = await File.ReadAllTextAsync(animalsFile, TestContext.Current.CancellationToken);
        content.ShouldContain("\"Woof\"");
        content.ShouldNotContain("\"Meow\"");
    }

    [Fact]
    public async Task InsertSymbol_After_WithContainingType_TargetsCorrectType()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string animalsFile = AnimalsFile(Fixture);
        var newMethod = "public string Purr() => \"Purrr\";";

        // Act — insert after Cat.Speak
        string result = await tools.InsertSymbol(animalsFile, "Speak", newMethod, containingType: "Cat", ct: TestContext.Current.CancellationToken);

        // Assert — new method appears after Cat.Speak, not after Dog.Speak
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(animalsFile, TestContext.Current.CancellationToken);
        content.ShouldContain("Purr");
        int catSpeakIndex = content.IndexOf("\"Meow\"", StringComparison.Ordinal);
        int purrIndex = content.IndexOf("Purr", StringComparison.Ordinal);
        int dogSpeakIndex = content.IndexOf("\"Woof\"", StringComparison.Ordinal);
        purrIndex.ShouldBeGreaterThan(catSpeakIndex);
        purrIndex.ShouldBeGreaterThan(dogSpeakIndex);
    }

    [Fact]
    public async Task InsertSymbol_Before_WithContainingType_TargetsCorrectType()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string animalsFile = AnimalsFile(Fixture);
        var newMethod = "public string Purr() => \"Purrr\";";

        // Act — insert before Cat.Speak
        string result = await tools.InsertSymbol(animalsFile, "Speak", newMethod, InsertPosition.Before, containingType: "Cat", ct: TestContext.Current.CancellationToken);

        // Assert — new method appears before Cat.Speak but after Dog.Speak
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(animalsFile, TestContext.Current.CancellationToken);
        int dogSpeakIndex = content.IndexOf("\"Woof\"", StringComparison.Ordinal);
        int purrIndex = content.IndexOf("Purr", StringComparison.Ordinal);
        int catSpeakIndex = content.IndexOf("\"Meow\"", StringComparison.Ordinal);
        purrIndex.ShouldBeGreaterThan(dogSpeakIndex);
        purrIndex.ShouldBeLessThan(catSpeakIndex);
    }

    [Fact]
    public async Task RenameSymbol_WithContainingType_TargetsCorrectType()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string animalsFile = AnimalsFile(Fixture);

        // Act — rename Cat.Name to CatName
        string result = await tools.RenameSymbol(animalsFile, "Name", "CatName", "Cat", ct: TestContext.Current.CancellationToken);

        // Assert — Cat.Name renamed, Dog.Name untouched
        result.ShouldContain("Renamed");
        string content = await File.ReadAllTextAsync(animalsFile, TestContext.Current.CancellationToken);
        content.ShouldContain("CatName");
        // Dog still has Name property
        content.ShouldContain("public string Name { get; } = \"Dog\"");
    }
}
