using Zphil.Roz.Models;
using Zphil.Roz.Tests.Fixtures;
using Zphil.Roz.Tools;
using Zphil.Roz.Utility;
using static Zphil.Roz.Tests.Fixtures.TestFileHelper;

namespace Zphil.Roz.Tests.Tools.CodeEditToolTests;

/// <summary>
///     F6: edit/replace tools must reject non-UTF-8 files (UTF-16 BOM, Windows-1252 high bytes)
///     with a friendly error instead of decoding to mojibake (U+FFFD) and writing the corruption
///     back. Legacy codebases — this server's explicit target — commonly contain such files.
/// </summary>
public class EncodingSafetyTests(EditWorkspaceFixture fixture) : EditTestBase(fixture)
{
    [Fact]
    public async Task ReplaceSymbol_Utf16File_RejectsAndLeavesBytesUnchanged()
    {
        // Arrange — Utf16Sample.cs is checked in as UTF-16 LE w/ BOM and loaded as a normal document
        // (MSBuildWorkspace decodes it correctly via the BOM). The edit path reads raw bytes itself.
        CodeEditTools tools = CreateEditTools(Fixture);
        string path = Utf16SampleFile(Fixture);
        byte[] before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        before[0].ShouldBe((byte)0xFF);
        before[1].ShouldBe((byte)0xFE); // precondition: genuine UTF-16 LE BOM

        // Act + Assert — friendly rejection naming the encoding; no corruption written.
        UserErrorException ex = await Should.ThrowAsync<UserErrorException>(() =>
            tools.ReplaceSymbol(path, "Value", "public int Value() => 43;", ct: TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("UTF-16");

        byte[] after = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        after.ShouldBe(before);
    }

    [Fact]
    public async Task ReplaceContent_Cp1252File_RejectsAndLeavesBytesUnchanged()
    {
        // Arrange — Cp1252Sample.cs holds Windows-1252 high bytes (0xE9/0xA9), invalid as UTF-8.
        // It is Compile-Remove'd (MSBuild never compiles it) but replace_content resolves it via
        // File.Exists, so the tool's own read is what must reject it.
        CodeEditTools tools = CreateEditTools(Fixture);
        string path = Cp1252SampleFile(Fixture);
        byte[] before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        // Act — replace_content batches; a per-op failure is surfaced inline in the formatted
        // result, not thrown (matches edit_symbol's per-op error contract).
        string result = await tools.ReplaceContent(
            [new ReplaceContentRequest(path, "anything", "x")], ct: TestContext.Current.CancellationToken);

        // Assert — friendly encoding error reported, no corruption written.
        // Today: the non-throwing decode mojibakes the high bytes, "anything" doesn't match, and the
        // op fails with a spurious "No matches found" (no encoding signal). With F6a the read rejects.
        result.ShouldContain("UTF-8");

        byte[] after = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        after.ShouldBe(before);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x00, 0x00 }, "UTF-32 LE")]
    [InlineData(new byte[] { 0x00, 0x00, 0xFE, 0xFF }, "UTF-32 BE")]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x41 }, "UTF-16 BE")]
    public async Task ReadFileWithEncoding_NonUtf8Bom_RejectsNamingTheEncoding(byte[] fileBytes, string encodingName)
    {
        // Arrange — a bare BOM is enough: detection reads only the leading bytes, and every non-UTF-8
        // BOM must reject before any decode. UTF-32 LE begins with the UTF-16 LE mark (FF FE), so
        // these cases also lock the documented UTF-32-before-UTF-16 detection order.
        string path = Path.Combine(Path.GetTempPath(), $"roz-mcp-encoding-{Guid.NewGuid():N}.cs");
        await File.WriteAllBytesAsync(path, fileBytes, TestContext.Current.CancellationToken);

        try
        {
            // Act + Assert — the rejection names the detected encoding so the user knows what to re-save.
            UnsupportedEncodingException ex = await Should.ThrowAsync<UnsupportedEncodingException>(() =>
                FileUtility.ReadFileWithEncodingAsync(path, TestContext.Current.CancellationToken));
            ex.Message.ShouldContain(encodingName);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
