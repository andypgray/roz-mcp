using Microsoft.CodeAnalysis;
using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Extensions;

/// <summary>
///     A single indexed document exposed for suffix-matching by <see cref="FilePathResolver" />.
/// </summary>
internal readonly record struct DocumentPathEntry(string AbsolutePath, string NormalizedRelativePath);

/// <summary>
///     Aggressive file-path resolution for MCP tools: literal resolution first, then
///     suffix-match against the workspace's documents for forgiving input.
/// </summary>
/// <remarks>
///     <para>
///         Agents routinely type paths with redundant leading directories (e.g.
///         <c>src/Libraries/Foo.cs</c> when the solution already lives under <c>src/</c>),
///         mixed slashes, or mismatched casing on Windows. A pure <see cref="PathExtensions.ResolveFilePath" />
///         call would combine the input against the solution directory and produce a bogus path like
///         <c>&lt;repo&gt;/src/src/Libraries/Foo.cs</c>, making every read/edit tool fail the first time.
///     </para>
///     <para>
///         This resolver tries the literal path first (covers all inputs that already work), then
///         falls back to suffix-matching against all solution documents. Comparison is
///         <see cref="StringComparison.OrdinalIgnoreCase" /> and must land on a path-segment boundary.
///     </para>
/// </remarks>
internal static class FilePathResolver
{
    /// <summary>
    ///     Resolves <paramref name="filePath" /> against the workspace's documents, returning an
    ///     absolute path. Throws <see cref="UserErrorException" /> on zero-match or ambiguous input.
    /// </summary>
    public static async Task<string> ResolveAgainstSolutionAsync(
        string filePath, WorkspaceManager workspace, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        string solutionDir = await workspace.GetRequiredSolutionDirectoryAsync(ct);
        string literalAbs = PathExtensions.ResolveFilePath(filePath, solutionDir);

        Solution solution = await workspace.GetSolutionAsync(ct);
        if (solution.GetDocumentByPath(literalAbs) is not null)
        {
            return literalAbs;
        }

        if (File.Exists(literalAbs))
        {
            return literalAbs;
        }

        string normalizedInput = NormalizeInput(filePath);
        string filename = Path.GetFileName(normalizedInput);
        IReadOnlyList<DocumentPathEntry> candidates = await workspace.GetDocumentsByFilenameAsync(filename, ct);

        // Bidirectional suffix check handles two input shapes:
        //   (1) user types a redundant prefix ("src/Libraries/Foo.cs" vs doc "Libraries/Foo.cs")
        //   (2) user types a short tail ("Foo.cs" vs doc "Libraries/Foo.cs")
        List<DocumentPathEntry> matches = candidates
            .Where(c => IsSegmentBoundarySuffix(c.NormalizedRelativePath, normalizedInput)
                        || IsSegmentBoundarySuffix(normalizedInput, c.NormalizedRelativePath))
            .ToList();

        if (matches.Count == 1)
        {
            return matches[0].AbsolutePath;
        }

        if (matches.Count == 0)
        {
            throw new UserErrorException(
                $"File not found in solution: '{filePath}'. " +
                $"Resolved path '{literalAbs}' does not exist on disk and no document matched by suffix. " +
                "Use find_symbol or get_workspace_info to verify the path.");
        }

        const int maxShown = 5;
        IEnumerable<string> shown = matches.Take(maxShown).Select(m => $"  - {m.NormalizedRelativePath}");
        string trailer = matches.Count > maxShown
            ? $"\n  ... and {matches.Count - maxShown} more"
            : "";
        throw new UserErrorException(
            $"Path '{filePath}' is ambiguous — matches {matches.Count} files:\n" +
            String.Join("\n", shown) + trailer + "\n" +
            "Use a longer prefix (e.g. include the project directory) to disambiguate.");
    }

    private static string NormalizeInput(string input)
    {
        string normalized = input.Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized.TrimStart('/');
    }

    /// <summary>
    ///     Returns true when <paramref name="shorter" /> is a segment-aligned suffix of
    ///     <paramref name="longer" /> — the prefix ends at a <c>/</c> boundary or the two
    ///     strings are equal. Rejects partial-segment matches like <c>ircle.cs</c>
    ///     against <c>Shapes/Circle.cs</c>.
    /// </summary>
    private static bool IsSegmentBoundarySuffix(string longer, string shorter)
    {
        if (longer.Length < shorter.Length)
        {
            return false;
        }

        if (!longer.EndsWith(shorter, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (longer.Length == shorter.Length)
        {
            return true;
        }

        int prefixLen = longer.Length - shorter.Length;
        return longer[prefixLen - 1] == '/';
    }
}
