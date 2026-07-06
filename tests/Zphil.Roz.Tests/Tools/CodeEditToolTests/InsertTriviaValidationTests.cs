using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     F4: the Insert trivia fallback (taken when content is not a parseable member) must reject content
///     that is neither a valid member nor pure trivia, instead of silently dropping the un-lexed tail and
///     reporting a false success. Pure comments / whitespace / #region directives still succeed.
/// </summary>
public class InsertTriviaValidationTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task InsertSymbol_MalformedMemberContent_ThrowsAndLeavesFileUnchanged()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = CircleFile(Fixture);
        byte[] before = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);

        // Act + Assert — "public void Broken( {" is not a parseable member and lexes to ZERO leading
        // trivia, so today it appends nothing yet reports success. It must reject instead.
        await Should.ThrowAsync<UserErrorException>(() =>
            tools.InsertSymbol(file, "Perimeter", "public void Broken( {", ct: TestContext.Current.CancellationToken));

        byte[] after = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
        after.ShouldBe(before);
    }

    [Fact]
    public async Task InsertSymbol_CommentPlusMalformedMember_ThrowsAndWritesNothingPartial()
    {
        // Arrange
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = CircleFile(Fixture);
        byte[] before = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);

        // Act + Assert — the comment lexes as trivia but the malformed member tail does not; today only
        // "// helper" would be written (partial), the tail silently dropped. Must reject, writing nothing.
        await Should.ThrowAsync<UserErrorException>(() =>
            tools.InsertSymbol(file, "Perimeter", "// helper\r\npublic void Broken( {", ct: TestContext.Current.CancellationToken));

        byte[] after = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
        after.ShouldBe(before);
    }

    [Fact]
    public async Task InsertSymbol_MultiLineCommentBlock_Succeeds()
    {
        // Arrange — a CRLF multi-line comment block is pure trivia and must round-trip (proves the
        // guard's Ordinal compare doesn't reject exact-CRLF trivia).
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = CircleFile(Fixture);

        // Act
        string result = await tools.InsertSymbol(
            file, "Perimeter", "// note line one\r\n// note line two\r\n", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("// note line one");
        content.ShouldContain("// note line two");
    }

    [Fact]
    public async Task InsertSymbol_RegionDirective_Succeeds()
    {
        // Arrange — #region/#endregion lex as directive trivia and must round-trip, not be rejected.
        CodeEditTools tools = CreateEditTools(Fixture);
        string file = CircleFile(Fixture);

        // Act
        string result = await tools.InsertSymbol(
            file, "Perimeter", "#region Notes\r\n// detail\r\n#endregion", ct: TestContext.Current.CancellationToken);

        // Assert
        result.ShouldContain("Inserted");
        string content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
        content.ShouldContain("#region Notes");
        content.ShouldContain("#endregion");
    }
}
