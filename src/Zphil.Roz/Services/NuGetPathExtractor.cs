namespace Zphil.Roz.Services;

/// <summary>
///     Extracts the package ID from an absolute path to a NuGet-cache assembly.
/// </summary>
/// <remarks>
///     The standard NuGet cache layout is <c>{root}/.nuget/packages/{id}/{version}/lib/{tfm}/{assembly}.dll</c>.
///     We match by walking path segments for <c>.nuget</c> followed immediately by <c>packages</c>, then
///     return the next segment lowercased — that gives us the package ID across user, machine, and
///     relocated caches without depending on the parent directory layout. Paths outside the cache
///     (HintPath, GAC, MSBuild SDK packs, framework refs) return null.
/// </remarks>
internal static class NuGetPathExtractor
{
    /// <summary>
    ///     Returns the package ID (lower-cased) extracted from <paramref name="path" />, or null when
    ///     <paramref name="path" /> is null/empty or does not point inside a NuGet cache.
    /// </summary>
    public static string? TryGetPackageId(string? path)
    {
        if (String.IsNullOrEmpty(path))
        {
            return null;
        }

        string[] segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        for (var i = 0; i < segments.Length - 2; i++)
        {
            if (!segments[i].Equals(".nuget", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!segments[i + 1].Equals("packages", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return segments[i + 2].ToLowerInvariant();
        }

        return null;
    }
}
