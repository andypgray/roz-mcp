using System.Text.RegularExpressions;

namespace Zphil.Roz.Extensions;

/// <summary>
///     File path resolution and glob pattern utilities.
/// </summary>
internal static class PathExtensions
{
    private static readonly char[] AnySeparator = ['/', '\\'];

    /// <summary>
    ///     Resolves a file path — if relative, resolves against the solution directory.
    /// </summary>
    public static string ResolveFilePath(string filePath, string solutionDir)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (Path.IsPathRooted(filePath))
        {
            return filePath;
        }

        string combinedPath = Path.Combine(solutionDir, filePath);
        return Path.GetFullPath(combinedPath);
    }

    /// <summary>
    ///     Returns true if the path contains glob wildcard characters (*, ?, [).
    /// </summary>
    public static bool IsGlobPattern(string path) =>
        path.Contains('*') || path.Contains('?') || path.Contains('[');

    /// <summary>
    ///     Returns true when <paramref name="path" /> contains <paramref name="segment" /> as a full
    ///     directory segment — delimited by separators (<c>/seg/</c> or <c>\seg\</c>) or appearing as
    ///     a leading segment (<c>seg/</c> or <c>seg\</c>). Matches both separators and is
    ///     case-insensitive, so it works on raw Roslyn paths (Windows backslashes) and pre-normalized
    ///     forward-slash paths alike.
    /// </summary>
    /// <remarks>
    ///     Segment-boundary aware: <c>objective/File.cs</c> does NOT match segment <c>obj</c>.
    /// </remarks>
    internal static bool ContainsDirectorySegment(string path, string segment) =>
        path.Contains($"/{segment}/", StringComparison.OrdinalIgnoreCase)
        || path.Contains($"\\{segment}\\", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith($"{segment}/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith($"{segment}\\", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Returns the final path segment, splitting on either separator regardless of the host OS.
    /// </summary>
    /// <remarks>
    ///     <see cref="Path.GetFileName(string)" /> only honors the platform separator, so a raw Roslyn
    ///     Windows path (backslashes) is treated as one segment on Linux. This mirrors
    ///     <see cref="ContainsDirectorySegment" />'s both-separator philosophy so classification stays
    ///     OS-independent.
    /// </remarks>
    internal static string GetFileNameAnySeparator(string path)
    {
        int lastSeparator = path.LastIndexOfAny(AnySeparator);
        return lastSeparator < 0 ? path : path[(lastSeparator + 1)..];
    }

    /// <summary>
    ///     Compiles a glob pattern (e.g. "*Tests*") into a <see cref="Regex" /> for matching.
    ///     Supports * (any characters) and ? (single character) wildcards.
    /// </summary>
    public static Regex CompileGlobRegex(string glob)
    {
        string regexPattern = "^" + Regex.Escape(glob)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    /// <summary>
    ///     Compiles a file-path glob pattern into a <see cref="Regex" /> for matching against
    ///     solution-relative paths. Supports ** (any directory depth), * (any characters within
    ///     a path segment), and ? (single character). Path separators are normalized to forward slashes.
    /// </summary>
    public static Regex CompileFilePathGlobRegex(string glob)
    {
        // Normalize separators to forward slash
        string normalized = glob.Replace('\\', '/');
        string escaped = Regex.Escape(normalized);

        // ** matches any number of directories (including none)
        string regexPattern = escaped
            .Replace("\\*\\*", "<<GLOBSTAR>>")
            .Replace("\\*", "[^/]*")
            .Replace("\\?", "[^/]")
            .Replace("<<GLOBSTAR>>", ".*");

        return new Regex("^" + regexPattern + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }
}
