using Zphil.Roz.Infrastructure;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Setup;

/// <summary>
///     Writes the Roslyn project-instructions snippet to a client's native rules file
///     (<c>CLAUDE.md</c> for Claude Code, <c>AGENTS.md</c> for Cursor / VS Code / Codex).
///     Creates the file if it does not exist, appends with a blank-line separator when the
///     section is absent, and otherwise replaces the existing <c># roz-mcp</c> section in
///     place so snippet updates propagate on re-run. Hand edits inside the section are
///     clobbered by design; content outside it is preserved byte-exactly.
/// </summary>
internal static class ProjectInstructionsConfigurator
{
    /// <summary>
    ///     Creates, appends, or replaces the Roslyn snippet section in the rules file at
    ///     <paramref name="projectRoot" />/<paramref name="rulesFileName" />.
    /// </summary>
    public static async Task WriteSnippetAsync(string projectRoot, string rulesFileName, CancellationToken ct = default)
    {
        string rulesFilePath = Path.Combine(projectRoot, rulesFileName);
        string snippet = ProjectInstructionsSnippet.Text;

        string content;
        if (File.Exists(rulesFilePath))
        {
            string existing = await File.ReadAllTextAsync(rulesFilePath, ct);
            int sectionStart = FindSectionStart(existing);
            if (sectionStart >= 0)
            {
                // Replace the whole section: splice the raw string rather than splitting into
                // lines and rejoining, which would normalize the user's line endings.
                int sectionEnd = FindSectionEnd(existing, sectionStart);
                string block = snippet.TrimEnd('\r', '\n') + (sectionEnd == existing.Length ? "\n" : "\n\n");
                content = existing[..sectionStart] + block + existing[sectionEnd..];
            }
            else
            {
                string separator = existing.EndsWith('\n') ? "\n" : "\n\n";
                content = existing + separator + snippet;
            }
        }
        else
        {
            content = snippet;
        }

        // Atomic temp-then-move so a crash mid-write can't truncate or corrupt the rules file.
        // Utf8NoBom (not Encoding.UTF8) keeps the output byte-identical to the prior plain
        // File.WriteAllTextAsync — File.WriteAllText defaults to UTF-8 without a BOM.
        await AtomicFileWriter.WriteAtomicAsync(rulesFilePath, content, FileUtility.Utf8NoBom, ct);

        Console.WriteLine($"  Updated: {Path.GetRelativePath(projectRoot, rulesFilePath)}");
    }

    /// <summary>
    ///     Start-of-line index of the first line beginning with
    ///     <see cref="ProjectInstructionsSnippet.SectionHeading" />, or -1 when absent.
    ///     Line-anchored (file start or a preceding newline), so a mid-line mention of the
    ///     heading text is not mistaken for the section.
    /// </summary>
    private static int FindSectionStart(string text)
    {
        var lineStart = 0;
        while (lineStart < text.Length)
        {
            if (text.AsSpan(lineStart).StartsWith(ProjectInstructionsSnippet.SectionHeading))
            {
                return lineStart;
            }

            int newline = text.IndexOf('\n', lineStart);
            if (newline < 0)
            {
                return -1;
            }

            lineStart = newline + 1;
        }

        return -1;
    }

    /// <summary>
    ///     End of the section starting at <paramref name="sectionStart" />: the start of the next
    ///     line beginning <c># </c> (exactly one hash plus a space, so <c>## </c> subsections stay
    ///     inside the section), or the end of the file.
    /// </summary>
    private static int FindSectionEnd(string text, int sectionStart)
    {
        int newline = text.IndexOf('\n', sectionStart);
        while (newline >= 0)
        {
            int lineStart = newline + 1;
            if (text.AsSpan(lineStart).StartsWith("# "))
            {
                return lineStart;
            }

            newline = text.IndexOf('\n', lineStart);
        }

        return text.Length;
    }
}
