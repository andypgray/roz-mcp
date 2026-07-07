using Zphil.Roz.Infrastructure;
using Zphil.Roz.Utility;

namespace Zphil.Roz.Setup;

/// <summary>
///     Writes the Roslyn project-instructions snippet to a client's native rules file
///     (<c>CLAUDE.md</c> for Claude Code, <c>AGENTS.md</c> for Cursor / VS Code / Codex).
///     Creates the file if it does not exist; otherwise appends with a blank-line separator,
///     skipping if the section heading is already present.
/// </summary>
internal static class ProjectInstructionsConfigurator
{
    /// <summary>
    ///     Creates or appends the Roslyn snippet to the rules file at
    ///     <paramref name="projectRoot" />/<paramref name="rulesFileName" />.
    /// </summary>
    public static async Task AppendSnippetAsync(string projectRoot, string rulesFileName, CancellationToken ct = default)
    {
        string rulesFilePath = Path.Combine(projectRoot, rulesFileName);
        string snippet = ProjectInstructionsSnippet.Text;

        string content;
        if (File.Exists(rulesFilePath))
        {
            string existing = await File.ReadAllTextAsync(rulesFilePath, ct);
            if (existing.Contains(ProjectInstructionsSnippet.SectionHeading, StringComparison.Ordinal))
            {
                Console.WriteLine($"  {rulesFileName} already contains Roslyn section — skipped.");
                return;
            }

            string separator = existing.EndsWith('\n') ? "\n" : "\n\n";
            content = existing + separator + snippet;
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
}
