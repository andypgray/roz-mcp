using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Zphil.Roz.Constants;
using Zphil.Roz.Models;
using Zphil.Roz.Services;
using Zphil.Roz.Symbols;

namespace Zphil.Roz.Extensions;

/// <summary>
///     Project and file classification: test detection, generated file checks, project type detection.
/// </summary>
internal static class ProjectExtensions
{
    private static readonly HashSet<string> TestFrameworkAssemblies =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "xunit.core",
            "xunit.assert",
            "nunit.framework",
            "Microsoft.VisualStudio.TestPlatform.TestFramework"
        };

    /// <summary>
    ///     Returns true when <paramref name="filePath" /> is a reference shipped by the .NET shared-framework
    ///     packs (the implicit <c>FrameworkReference</c>s — <c>Microsoft.NETCore.App</c>,
    ///     <c>Microsoft.AspNetCore.App</c>, <c>Microsoft.WindowsDesktop.App</c>). These are part of the
    ///     runtime, not user-declared package or project references, so unused-reference detection must
    ///     ignore them.
    /// </summary>
    /// <remarks>
    ///     The reliable signal is the <c>packs</c> directory in the SDK install
    ///     (e.g. <c>{dotnet}/packs/Microsoft.NETCore.App.Ref/{version}/ref/{tfm}/System.Runtime.dll</c>).
    ///     We also check the <c>.Ref</c> pack folder names directly so non-standard SDK layouts still work.
    /// </remarks>
    internal static bool IsFrameworkReference(string? filePath)
    {
        if (String.IsNullOrEmpty(filePath))
        {
            return false;
        }

        if (filePath.Contains("/packs/", StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains("\\packs\\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return filePath.Contains("Microsoft.NETCore.App.Ref", StringComparison.OrdinalIgnoreCase)
               || filePath.Contains("Microsoft.AspNetCore.App.Ref", StringComparison.OrdinalIgnoreCase)
               || filePath.Contains("Microsoft.WindowsDesktop.App.Ref", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Returns true if the project references a known test framework assembly (xUnit, NUnit, MSTest)
    ///     or matches a user-configured test path/namespace prefix.
    /// </summary>
    public static bool IsTestProject(this Project project)
    {
        if (TestClassifier.IsConfiguredAsTest(project))
        {
            return true;
        }

        return project.MetadataReferences
            .OfType<PortableExecutableReference>()
            .Any(r => r.FilePath is not null &&
                      TestFrameworkAssemblies.Contains(
                          Path.GetFileNameWithoutExtension(r.FilePath)));
    }

    /// <summary>
    ///     Detects the project type by checking test framework references and parsing the csproj file
    ///     for SDK name and key properties. Returns null if the type cannot be determined.
    /// </summary>
    public static string? DetectProjectType(this Project project)
    {
        if (project.IsTestProject())
        {
            return "test";
        }

        // UI-framework floor from resolved metadata references — mirrors the IsTestProject
        // heuristic. This survives a missing/malformed csproj so a WinForms or WPF project
        // still classifies correctly (read-only is forgiving).
        List<string> referencedAssemblies = project.MetadataReferences
            .OfType<PortableExecutableReference>()
            .Where(r => r.FilePath is not null)
            .Select(r => Path.GetFileNameWithoutExtension(r.FilePath!))
            .ToList();
        string? uiFloor = DetectUiFramework(referencedAssemblies);

        if (TryLoadCsproj(project)?.Root is not { } projectElement)
        {
            return uiFloor;
        }

        try
        {
            string? sdk = projectElement.Attribute("Sdk")?.Value;

            // SDK-specific kinds are only meaningful when an Sdk attribute is present.
            // Non-SDK legacy .NET Framework projects have no Sdk attribute and do not
            // auto-define UI properties — fall through to the property/reference scan
            // rather than early-returning null.
            if (sdk is not null)
            {
                if (sdk.Equals("Microsoft.NET.Sdk.BlazorWebAssembly", StringComparison.OrdinalIgnoreCase))
                {
                    return "blazor-wasm";
                }

                if (sdk.Equals("Microsoft.NET.Sdk.Web", StringComparison.OrdinalIgnoreCase))
                {
                    return "web";
                }

                if (sdk.Equals("Microsoft.NET.Sdk.Worker", StringComparison.OrdinalIgnoreCase))
                {
                    return "worker";
                }

                if (sdk.Equals("Microsoft.NET.Sdk.Razor", StringComparison.OrdinalIgnoreCase))
                {
                    return "razor";
                }

                if (sdk.Equals("Microsoft.NET.Sdk.Maui", StringComparison.OrdinalIgnoreCase))
                {
                    return "maui";
                }

                // Microsoft.NET.Sdk.WindowsDesktop was used by .NET Core 3.x WPF/WinForms
                // projects before the properties were moved to the base SDK. Fall through.
                if (!sdk.Equals("Microsoft.NET.Sdk", StringComparison.OrdinalIgnoreCase) &&
                    !sdk.Equals("Microsoft.NET.Sdk.WindowsDesktop", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }
            }

            // Materialize once: scanned for PropertyGroups and ItemGroups below.
            // Namespace-agnostic — non-SDK csproj declares the legacy 2003 MSBuild namespace.
            List<XElement> projectChildren = projectElement.Elements().ToList();

            string? outputType = null;
            var useWpf = false;
            var useWinForms = false;
            var useMaui = false;
            var hasAzureFunctions = false;

            foreach (XElement pg in projectChildren.Where(e =>
                         e.Name.LocalName.Equals("PropertyGroup", StringComparison.OrdinalIgnoreCase)))
            {
                outputType ??= FindElement(pg, "OutputType")?.Value;
                useWpf = useWpf || IsTrue(FindElement(pg, "UseWPF")?.Value);
                useWinForms = useWinForms || IsTrue(FindElement(pg, "UseWindowsForms")?.Value);
                useMaui = useMaui || IsTrue(FindElement(pg, "UseMaui")?.Value);
                hasAzureFunctions = hasAzureFunctions || FindElement(pg, "AzureFunctionsVersion") is not null;
            }

            // .NET Framework UI convention: no UseWindowsForms/UseWPF property, just
            // <Reference Include="System.Windows.Forms" />. The assembly simple name is
            // the token before the first comma of the Include attribute. Merged with the
            // metadata-reference floor.
            List<string> referencedAssemblyNames = projectChildren
                .Where(e => e.Name.LocalName.Equals("ItemGroup", StringComparison.OrdinalIgnoreCase))
                .SelectMany(ig => ig.Elements())
                .Where(e => e.Name.LocalName.Equals("Reference", StringComparison.OrdinalIgnoreCase))
                .Select(e => e.Attribute("Include")?.Value)
                .Where(v => !String.IsNullOrWhiteSpace(v))
                .Select(v => v!.Split(',')[0].Trim())
                .ToList();
            string? referenceUi = DetectUiFramework(referencedAssemblyNames);

            if (useMaui)
            {
                return "maui";
            }

            if (useWpf || referenceUi == "wpf" || uiFloor == "wpf")
            {
                return "wpf";
            }

            if (useWinForms || referenceUi == "winforms" || uiFloor == "winforms")
            {
                return "winforms";
            }

            if (hasAzureFunctions)
            {
                return "azure-functions";
            }

            if (outputType is not null &&
                (outputType.Equals("Exe", StringComparison.OrdinalIgnoreCase) ||
                 outputType.Equals("WinExe", StringComparison.OrdinalIgnoreCase)))
            {
                return "console";
            }

            return "classlib";
        }
        catch
        {
            // Csproj files may be malformed, locked, or use non-standard schemas — fall
            // back to the UI-framework floor rather than discarding a usable signal.
            return uiFloor;
        }
    }

    /// <summary>
    ///     Returns true if the file path points to a build-generated file.
    /// </summary>
    /// <remarks>
    ///     Based on Roslyn's internal heuristics for generated code files:
    ///     https://github.com/dotnet/roslyn/blob/main/src/Compilers/Core/Portable/SourceGeneration/GeneratedCodeUtilities.cs
    /// </remarks>
    public static bool IsGeneratedFile(string? filePath)
    {
        if (String.IsNullOrEmpty(filePath))
        {
            return false;
        }

        // Build artifacts live under obj/ (full directory segment, either separator)
        if (PathExtensions.ContainsDirectorySegment(filePath, "obj"))
        {
            return true;
        }

        // Split on either separator: on Linux, Path.GetFileName treats a raw Roslyn Windows path
        // (backslashes) as a single segment, so a generated file behind backslashes reads as source.
        string fileName = PathExtensions.GetFileNameAnySeparator(filePath);

        // VS designer transient files
        if (fileName.StartsWith("TemporaryGeneratedFile_", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Check filename-without-extension for generated suffixes (matches Roslyn's heuristics)
        string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        if (nameWithoutExt.EndsWith(".designer", StringComparison.OrdinalIgnoreCase) ||
            nameWithoutExt.EndsWith(".generated", StringComparison.OrdinalIgnoreCase) ||
            nameWithoutExt.EndsWith(".g", StringComparison.OrdinalIgnoreCase) ||
            nameWithoutExt.EndsWith(".g.i", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Returns true if the given source location is in a generated file.
    /// </summary>
    public static bool IsInGeneratedFile(this Location location) => location.IsInSource && IsGeneratedFile(location.GetLineSpan().Path);

    /// <summary>
    ///     Returns true if the symbol's primary source location is in a generated file.
    /// </summary>
    public static bool IsInGeneratedFile(this ISymbol symbol)
    {
        Location? location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        return location is not null && location.IsInGeneratedFile();
    }

    /// <summary>
    ///     Returns the best source location for display, preferring non-generated files.
    ///     For partial types with both user-authored and generated declarations, this avoids
    ///     showing confusing line numbers from generated files like LoggerMessage.g.cs.
    /// </summary>
    public static Location? PreferNonGeneratedSourceLocation(this ISymbol symbol)
    {
        Location? first = null;
        foreach (Location location in symbol.Locations)
        {
            if (!location.IsInSource)
            {
                continue;
            }

            if (!location.IsInGeneratedFile())
            {
                return location;
            }

            first ??= location;
        }

        return first;
    }

    /// <summary>
    ///     Returns true if the symbol's primary source location is in a test project.
    /// </summary>
    public static bool IsInTestProject(this ISymbol symbol, Solution solution)
    {
        Location? location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location?.SourceTree?.FilePath is null)
        {
            return false;
        }

        string fullPath = Path.GetFullPath(location.SourceTree.FilePath);
        Document? doc = solution.GetDocumentByPath(fullPath);
        return doc?.Project.IsTestProject() ?? false;
    }

    /// <summary>
    ///     Finds a child element by local name, case-insensitive.
    ///     MSBuild property names are case-insensitive, but XML element names are case-sensitive,
    ///     so csproj files may use any casing (e.g. UseWPF, UseWpf, useWpf).
    /// </summary>
    private static XElement? FindElement(XElement parent, string localName) =>
        parent.Elements().FirstOrDefault(e =>
            e.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    ///     Loads <paramref name="project" />'s csproj as an <see cref="XDocument" />.
    /// </summary>
    /// <remarks>
    ///     Returns null when the project has no file path or the file is missing, locked, or
    ///     malformed — csproj metadata is best-effort on the read-only info path, so a load
    ///     failure degrades to "unknown" rather than throwing.
    /// </remarks>
    internal static XDocument? TryLoadCsproj(Project project)
    {
        if (project.FilePath is null)
        {
            return null;
        }

        try
        {
            return XDocument.Load(project.FilePath);
        }
        catch
        {
            // A missing, locked, or non-well-formed csproj is expected and safe to swallow:
            // metadata is best-effort here, so degrade to "unknown" instead of crashing.
            return null;
        }
    }

    /// <summary>
    ///     Reads the target framework from a parsed csproj.
    /// </summary>
    /// <remarks>
    ///     Priority: <c>&lt;TargetFramework&gt;</c> → first entry of
    ///     <c>&lt;TargetFrameworks&gt;</c> → <c>&lt;TargetFrameworkVersion&gt;</c> (legacy
    ///     non-SDK, converted via <see cref="NormalizeFrameworkVersion" />). Element matching is
    ///     namespace-agnostic, so the legacy 2003 MSBuild namespace is handled. Returns null
    ///     when none of the three elements are present.
    /// </remarks>
    internal static string? GetTargetFrameworkFromCsproj(XDocument doc)
    {
        if (doc.Root is not { } root)
        {
            return null;
        }

        List<XElement> propertyGroups = root.Elements()
            .Where(e => e.Name.LocalName.Equals("PropertyGroup", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (FirstNonEmptyElementValue(propertyGroups, "TargetFramework") is { } single)
        {
            return single.ToLowerInvariant();
        }

        if (FirstNonEmptyElementValue(propertyGroups, "TargetFrameworks") is { } multi)
        {
            string? first = multi
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (first is not null)
            {
                return first.ToLowerInvariant();
            }
        }

        if (FirstNonEmptyElementValue(propertyGroups, "TargetFrameworkVersion") is { } legacy)
        {
            return NormalizeFrameworkVersion(legacy);
        }

        return null;
    }

    /// <summary>
    ///     Returns the trimmed value of the first <paramref name="localName" /> element found
    ///     across <paramref name="propertyGroups" />, skipping empty/whitespace values.
    /// </summary>
    private static string? FirstNonEmptyElementValue(IEnumerable<XElement> propertyGroups, string localName)
    {
        foreach (XElement pg in propertyGroups)
        {
            string? value = FindElement(pg, localName)?.Value;
            if (!String.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    /// <summary>
    ///     Converts a legacy <c>&lt;TargetFrameworkVersion&gt;</c> value (e.g. <c>v4.8</c>) to a
    ///     TFM moniker (e.g. <c>net48</c>).
    /// </summary>
    /// <remarks>
    ///     Strips a leading <c>v</c>/<c>V</c>, drops dots, and prefixes <c>net</c>:
    ///     <c>v4.7.2</c>→<c>net472</c>, <c>v3.5</c>→<c>net35</c>, <c>v2.0</c>→<c>net20</c>.
    /// </remarks>
    internal static string NormalizeFrameworkVersion(string value)
    {
        string trimmed = value.Trim();
        string digits = trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? trimmed[1..]
            : trimmed;
        return $"net{digits.Replace(".", "")}";
    }

    /// <summary>
    ///     Maps a set of referenced assembly simple names to a UI-framework tag:
    ///     <c>PresentationFramework</c> → <c>wpf</c>, <c>System.Windows.Forms</c> → <c>winforms</c>.
    /// </summary>
    /// <remarks>
    ///     Returns null when neither assembly is referenced. WPF is checked first because WPF
    ///     apps also reference the WinForms interop assembly.
    /// </remarks>
    internal static string? DetectUiFramework(IReadOnlyCollection<string> assemblySimpleNames)
    {
        if (assemblySimpleNames.Contains("PresentationFramework", StringComparer.OrdinalIgnoreCase))
        {
            return "wpf";
        }

        if (assemblySimpleNames.Contains("System.Windows.Forms", StringComparer.OrdinalIgnoreCase))
        {
            return "winforms";
        }

        return null;
    }

    /// <summary>
    ///     Strips the <c>(tfm)</c> suffix from Roslyn multi-TFM project names.
    ///     e.g. <c>Orleans.Core(net8.0)</c> → <c>Orleans.Core</c>.
    /// </summary>
    internal static string StripTfmSuffix(string projectName)
    {
        int parenIndex = projectName.LastIndexOf('(');
        return parenIndex > 0 && projectName.EndsWith(')')
            ? projectName[..parenIndex]
            : projectName;
    }

    /// <summary>
    ///     Counts distinct test projects in <paramref name="projects" />, merging multi-TFM
    ///     entries by their stripped name so the count matches what a human would count
    ///     from the <c>.sln</c> file.
    /// </summary>
    internal static int CountDistinctTestProjects(IEnumerable<Project> projects) =>
        projects.Where(p => p.IsTestProject())
            .Select(p => StripTfmSuffix(p.Name))
            .Distinct(StringComparer.Ordinal)
            .Count();

    /// <summary>
    ///     Truncates <paramref name="symbols" /> to <paramref name="maxResults" /> and — only when
    ///     truncation actually happens — computes the per-project distribution over the pre-truncation
    ///     list so the summary covers every match. Callers that need both values get them in one step.
    /// </summary>
    internal static (List<ISymbol> Truncated, int TotalCount, IReadOnlyList<ProjectDistributionEntry>? Distribution)
        TruncateWithDistribution(this List<ISymbol> symbols, int? maxResults, Solution solution)
    {
        IReadOnlyList<ProjectDistributionEntry>? distribution = null;
        if (maxResults.HasValue && symbols.Count > maxResults.Value)
        {
            distribution = ComputeSymbolDistribution(symbols, solution);
        }

        (List<ISymbol> truncated, int totalCount) = symbols.Truncate(maxResults);
        return (truncated, totalCount, distribution);
    }

    /// <summary>
    ///     Computes per-project distribution for a list of symbols before truncation.
    ///     Maps each symbol to its declaring project via <see cref="Solution.GetDocument(SyntaxTree?)" />.
    /// </summary>
    private static IReadOnlyList<ProjectDistributionEntry> ComputeSymbolDistribution(
        IEnumerable<ISymbol> symbols, Solution solution)
    {
        return symbols
            .Select(s =>
            {
                SyntaxTree? tree = s.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree
                                   ?? s.Locations.Select(l => l.SourceTree).OfType<SyntaxTree>().FirstOrDefault();
                return tree is not null && solution.GetDocument(tree) is { } d
                    ? StripTfmSuffix(d.Project.Name)
                    : "Unknown";
            })
            .GroupBy(projectName => projectName)
            .Select(g => new ProjectDistributionEntry(g.Key, g.Count(), 0))
            .OrderByDescending(e => e.ReferenceCount)
            .ToList();
    }

    private static bool IsTrue(string? value) =>
        value is not null && value.Equals("true", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Returns the projects whose names contain <paramref name="project" /> as a
    ///     case-insensitive substring.
    /// </summary>
    /// <remarks>
    ///     Returns all projects when <paramref name="project" /> is null. Throws
    ///     <see cref="UserErrorException" /> when a filter is supplied but matches no projects.
    /// </remarks>
    internal static IReadOnlyList<Project> FilterByProjectName(this Solution solution, string? project)
    {
        if (project is null)
        {
            return solution.Projects.ToList();
        }

        List<Project> scoped = solution.Projects
            .Where(p => p.Name.Contains(project, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (scoped.Count == 0)
        {
            throw new UserErrorException(ErrorMessages.ProjectNotFound(project, GetSortedProjectNames(solution)));
        }

        return scoped;
    }

    /// <summary>
    ///     Throws <see cref="UserErrorException" /> when no project name in the solution matches
    ///     <paramref name="project" /> (case-insensitive substring).
    /// </summary>
    /// <remarks>
    ///     For cursor/location resolution the <c>project</c> filter has no resolution role (see
    ///     <see cref="Symbols.SymbolResolver.IsPositionalResolution" />), so reference/impact tools call
    ///     this to keep a typo'd project name an error even though the filter no longer narrows results.
    ///     Name-based resolution validates the project via <see cref="FilterByProjectName" /> instead.
    /// </remarks>
    internal static void EnsureProjectMatchExists(this Solution solution, string project)
    {
        if (!solution.Projects.Any(p => p.Name.Contains(project, StringComparison.OrdinalIgnoreCase)))
        {
            throw new UserErrorException(ErrorMessages.ProjectNotFound(project, GetSortedProjectNames(solution)));
        }
    }

    /// <summary>
    ///     For reference/impact tools whose <c>project</c> filter only scopes name-based resolution:
    ///     returns true when a <c>project</c> was supplied but resolution ran positionally (cursor
    ///     mode), so the filter had no effect. Validates that the project name still exists (a typo
    ///     stays an error) before reporting the miss, so the caller can surface an "ignored" note.
    ///     Returns false when no filter was supplied or it participated in resolution.
    /// </summary>
    internal static bool ProjectFilterIgnoredForPositionalResolution(
        this Solution solution, string? project, string? symbolName, string? filePath, int? line, int? column)
    {
        if (project is null || !SymbolResolver.IsPositionalResolution(symbolName, filePath, line, column))
        {
            return false;
        }

        solution.EnsureProjectMatchExists(project);
        return true;
    }

    private static List<string> GetSortedProjectNames(Solution solution) =>
        solution.Projects.Select(p => p.Name).OrderBy(n => n).ToList();
}
