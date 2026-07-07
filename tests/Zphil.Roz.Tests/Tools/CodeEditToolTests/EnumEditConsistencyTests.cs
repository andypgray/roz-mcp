using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     Verifies consistency of enum edit operations with non-enum operations:
///     no-op detection, scoped formatting (not reformatting unrelated code).
/// </summary>
public class EnumEditConsistencyTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    // ── no-op detection ──────────────────────────────────────────────────

    [Fact]
    public async Task ReplaceEnumMember_IdenticalDeclaration_ReturnsNoOp()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string typeKindFile = TypeKindExamplesFile(Fixture);
        string originalContent = await File.ReadAllTextAsync(typeKindFile, TestContext.Current.CancellationToken);

        // Act — replace Red with itself
        string result = await tools.ReplaceSymbol(typeKindFile, "Red", "Red", ct: TestContext.Current.CancellationToken);

        // Assert — should report no-op, file unchanged
        result.ShouldContain("No changes made");
        result.ShouldContain("identical");
        string afterContent = await File.ReadAllTextAsync(typeKindFile, TestContext.Current.CancellationToken);
        afterContent.ShouldBe(originalContent);
    }

    [Fact]
    public async Task ReplaceEnumMember_IdenticalWithExplicitValue_ReturnsNoOp()
    {
        // Arrange — give Red an explicit value first
        string typeKindFile = TypeKindExamplesFile(Fixture);
        await RewriteFileAsync(Fixture, typeKindFile, c => c.Replace("    Red,", "    Red = 0,"));

        CodeEditTools tools = CreateEditTools(Fixture);
        string originalContent = await File.ReadAllTextAsync(typeKindFile, TestContext.Current.CancellationToken);

        // Act — replace with identical declaration
        string result = await tools.ReplaceSymbol(typeKindFile, "Red", "Red = 0", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("No changes made");
        result.ShouldContain("identical");
        string afterContent = await File.ReadAllTextAsync(typeKindFile, TestContext.Current.CancellationToken);
        afterContent.ShouldBe(originalContent);
    }

    // ── scoped formatting ────────────────────────────────────────────────

    [Fact]
    public Task ReplaceEnumMember_ScopedFormatting_DoesNotReformatUnrelatedCode() =>
        AssertEnumEdit_DoesNotReformatUnrelatedMember((tools, file) => tools.ReplaceSymbol(file, "Red", "Crimson = 10", ct: TestContext.Current.CancellationToken));

    [Fact]
    public Task InsertEnumMember_ScopedFormatting_DoesNotReformatUnrelatedCode() =>
        AssertEnumEdit_DoesNotReformatUnrelatedMember((tools, file) => tools.InsertSymbol(file, "Yellow", "Purple", ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task RemoveEnumMember_SingleLineMember_ReportsOneLineNotTriviaOvercount()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string typeKindFile = TypeKindExamplesFile(Fixture);

        // Act — remove a single-line enum member.
        string result = await tools.RemoveSymbol(typeKindFile, "Yellow", ct: TestContext.Current.CancellationToken);

        // Assert — CR-21: counted as 1 line. The old ToFullString() included the member's leading
        // newline/indent trivia and overcounted it as 2 lines; ToString() counts just the declaration.
        result.ShouldContain("Removed 'Yellow'");
        result.ShouldContain("(1 lines)");
        result.ShouldNotContain("(2 lines)");
    }

    [Fact]
    public Task RemoveEnumMember_ScopedFormatting_DoesNotReformatUnrelatedCode() =>
        AssertEnumEdit_DoesNotReformatUnrelatedMember((tools, file) => tools.RemoveSymbol(file, "Yellow", ct: TestContext.Current.CancellationToken));

    /// <summary>
    ///     Shared arrange/assert for the scoped-formatting cases: an unrelated member (DistanceTo) is
    ///     given non-standard spacing, an enum <paramref name="edit" /> runs, and the odd spacing must
    ///     survive (the edit must not reformat unrelated code). Each caller exercises a distinct
    ///     production path (Replace/Insert/Remove), so the three [Fact]s are kept separate.
    /// </summary>
    private async Task AssertEnumEdit_DoesNotReformatUnrelatedMember(Func<CodeEditTools, string, Task> edit)
    {
        // Arrange — add deliberately non-standard formatting to an unrelated member (DistanceTo)
        string typeKindFile = TypeKindExamplesFile(Fixture);
        await RewriteFileAsync(Fixture, typeKindFile, c => c.Replace(
            "    public double DistanceTo(Point other) =>",
            "    public double DistanceTo(  Point   other  ) =>"));

        CodeEditTools tools = CreateEditTools(Fixture);
        string beforeContent = await File.ReadAllTextAsync(typeKindFile, TestContext.Current.CancellationToken);
        beforeContent.ShouldContain("DistanceTo(  Point   other  )");

        // Act — perform the enum edit (unrelated to DistanceTo)
        await edit(tools, typeKindFile);

        // Assert — the oddly-formatted DistanceTo signature is untouched
        string afterContent = await File.ReadAllTextAsync(typeKindFile, TestContext.Current.CancellationToken);
        afterContent.ShouldContain("DistanceTo(  Point   other  )");
    }
}
