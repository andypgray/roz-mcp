using System.Reflection;

namespace Zphil.Roz.Infrastructure;

/// <summary>
///     Loads the embedded project-instructions-snippet.md resource for writing to
///     per-client project-instructions files (CLAUDE.md, AGENTS.md).
/// </summary>
internal static class ProjectInstructionsSnippet
{
    /// <summary>The section heading used to detect whether the snippet is already present.</summary>
    internal const string SectionHeading = "# roz-mcp";

    internal static readonly string Text = LoadEmbeddedResource();

    private static string LoadEmbeddedResource()
    {
        Assembly assembly = typeof(ProjectInstructionsSnippet).Assembly;
        using Stream stream = assembly.GetManifestResourceStream("Zphil.Roz.project-instructions-snippet.md")
                              ?? throw new InvalidOperationException("Embedded resource 'Zphil.Roz.project-instructions-snippet.md' not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
