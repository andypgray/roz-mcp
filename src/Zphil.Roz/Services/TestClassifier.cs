using Microsoft.CodeAnalysis;
using Zphil.Roz.Extensions;
using Zphil.Roz.Infrastructure;

namespace Zphil.Roz.Services;

/// <summary>
///     Classifies projects as test projects based on user-configured path prefixes and namespace prefixes,
///     supplementing the assembly-reference heuristic in <see cref="ProjectExtensions.IsTestProject" />.
/// </summary>
internal static class TestClassifier
{
    private static IReadOnlyList<string> s_testPathPrefixes = ParseEnvVar(RozEnvVars.TestPaths.Name);
    private static IReadOnlyList<string> s_testNamespacePrefixes = ParseEnvVar(RozEnvVars.TestNamespaces.Name);

    /// <summary>
    ///     Returns true if the project matches any user-configured test path prefix or namespace prefix.
    /// </summary>
    internal static bool IsConfiguredAsTest(Project project)
    {
        if (s_testPathPrefixes.Count > 0 && MatchesPathPrefix(project))
        {
            return true;
        }

        if (s_testNamespacePrefixes.Count > 0 && MatchesNamespacePrefix(project))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Test-only seam: replaces the env-var-driven prefix lists for the duration of a test.
    ///     Pass null to reset to env-var values. <see cref="ProjectExtensions.IsTestProject" /> is
    ///     called from cross-collection ref/nav paths — exposing this as an env var would broaden
    ///     a parallel-test race; the typed setter keeps overrides scoped to the calling fixture.
    /// </summary>
    internal static void SetOverrides(IReadOnlyList<string>? pathPrefixes, IReadOnlyList<string>? namespacePrefixes)
    {
        s_testPathPrefixes = pathPrefixes ?? ParseEnvVar(RozEnvVars.TestPaths.Name);
        s_testNamespacePrefixes = namespacePrefixes ?? ParseEnvVar(RozEnvVars.TestNamespaces.Name);
    }

    private static bool MatchesPathPrefix(Project project)
    {
        if (project.FilePath is null)
        {
            return false;
        }

        string? solutionDir = project.Solution.FilePath is not null
            ? Path.GetDirectoryName(project.Solution.FilePath)
            : null;

        if (solutionDir is null)
        {
            return false;
        }

        string projectPath = Path.GetFullPath(project.FilePath);
        string solutionDirFull = Path.GetFullPath(solutionDir);

        if (!projectPath.StartsWith(solutionDirFull, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string relativePath = projectPath[solutionDirFull.Length..]
            .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');

        foreach (string prefix in s_testPathPrefixes)
        {
            if (relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                relativePath.Length > prefix.Length &&
                relativePath[prefix.Length] == '/')
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesNamespacePrefix(Project project)
    {
        string assemblyName = project.AssemblyName;
        if (String.IsNullOrEmpty(assemblyName))
        {
            return false;
        }

        foreach (string prefix in s_testNamespacePrefixes)
        {
            if (assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (assemblyName.Length == prefix.Length || assemblyName[prefix.Length] == '.'))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> ParseEnvVar(string envVarName) =>
        EnvParse.DelimitedList(envVarName)
            .Select(s => s.Replace('\\', '/').TrimEnd('/'))
            .Where(s => s.Length > 0)
            .ToList();

    /// <summary>
    ///     Splits a raw env-var value into a normalised prefix list. Accepts comma- and
    ///     semicolon-delimited values (matching <see cref="Pipeline.ToolSelector" />'s
    ///     convention) so users don't have to remember per-variable delimiter rules.
    /// </summary>
    internal static IReadOnlyList<string> ParsePrefixes(string? value)
    {
        if (String.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Replace('\\', '/').TrimEnd('/'))
            .Where(s => s.Length > 0)
            .ToList();
    }
}
