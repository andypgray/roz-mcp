using Microsoft.Extensions.Logging.Abstractions;
using Zphil.Roz.Enums;
using Zphil.Roz.Services;
using Zphil.Roz.Symbols;
using Zphil.Roz.Tools;

namespace Zphil.Roz.Tests.Fixtures;

/// <summary>
///     Shared file path, content, and tool factory helpers for edit/using tool tests.
/// </summary>
internal static class TestFileHelper
{
    // ── Reference-kind alias ──────────────────────────────────────────────

    /// <summary>Shorthand for <see cref="ReferenceKind.Invocations" /> in reference-resolution tests.</summary>
    internal const ReferenceKind Invocations = ReferenceKind.Invocations;

    // ── Edit fixture samples ──────────────────────────────────────────────

    /// <summary>
    ///     Test-Corp copyright header prepended to fixtures that verify header preservation across edits.
    /// </summary>
    internal const string CopyrightHeader =
        "// Copyright (c) Test Corp. All rights reserved.\r\n// Licensed under the MIT License.\r\n\r\n";

    /// <summary>
    ///     Exact copy of ILSpy's TestPlugin/CustomLanguage.cs that triggered the trivia bug.
    ///     Tab-indented, K&amp;R brace style, doc comments on the class, two using groups
    ///     separated by a blank line. Types are stubbed (class named Circle) so the file
    ///     compiles against the test fixture solution.
    /// </summary>
    internal const string CustomLanguageCs =
        "// Copyright (c) AlphaSierraPapa for the SharpDevelop Team\r\n"
        + "// This code is distributed under MIT X11 license\r\n"
        + "\r\n"
        + "using System;\r\n"
        + "\r\n"
        + "namespace TestFixture.Shapes\r\n"
        + "{\r\n"
        + "\t/// <summary>\r\n"
        + "\t/// Adds a new language to the decompiler.\r\n"
        + "\t/// </summary>\r\n"
        + "\tpublic class Circle\r\n"
        + "\t{\r\n"
        + "\t\tpublic string Name {\r\n"
        + "\t\t\tget {\r\n"
        + "\t\t\t\treturn \"Custom\";\r\n"
        + "\t\t\t}\r\n"
        + "\t\t}\r\n"
        + "\r\n"
        + "\t\tpublic string FileExtension {\r\n"
        + "\t\t\tget {\r\n"
        + "\t\t\t\t// used in 'Save As' dialog\r\n"
        + "\t\t\t\treturn \".txt\";\r\n"
        + "\t\t\t}\r\n"
        + "\t\t}\r\n"
        + "\r\n"
        + "\t\tpublic string Describe()\r\n"
        + "\t\t{\r\n"
        + "\t\t\treturn \"hello\";\r\n"
        + "\t\t}\r\n"
        + "\t}\r\n"
        + "}\r\n";

    /// <summary>
    ///     NuGet restore metadata copied out of <c>obj/</c> alongside the generated sources. Without these,
    ///     an MSBuild design-time build in the temp copy cannot resolve <c>PackageReference</c>s or the
    ///     <c>&lt;Analyzer&gt;</c> items the analyzer packs inject — so a fixture project's analyzers (e.g.
    ///     xunit.analyzers) never run. They reference packages by the machine-global packages folder, so
    ///     they stay valid after the project moves. (MSBuildWorkspace never invokes Restore, so the
    ///     restore <c>project.nuget.cache</c> is deliberately omitted.)
    /// </summary>
    private static readonly string[] RestoreArtifactPatterns =
        ["project.assets.json", "*.nuget.g.props", "*.nuget.g.targets"];

    /// <summary>
    ///     Composes a <c>location</c> string for tool calls (e.g. <c>"Foo.cs:42:18"</c>).
    /// </summary>
    internal static string Loc(string filePath, int? line = null, int? column = null) =>
        LocationFormat.Format(filePath, line, column);

    // ── Tool factories ────────────────────────────────────────────────────

    internal static CodeEditTools CreateEditTools(ITestWorkspace ws)
    {
        var resolution = new EditSymbolResolver(ws.WorkspaceManager);
        var verification = new EditVerificationService(ws.WorkspaceManager);
        return new CodeEditTools(
            new SymbolEditService(ws.WorkspaceManager, ws.BaselineManager, resolution, verification, NullLogger<SymbolEditService>.Instance),
            new RenameService(ws.WorkspaceManager, ws.BaselineManager, resolution, verification),
            new TextReplacementService(ws.WorkspaceManager, ws.BaselineManager, verification),
            new CodeFixService(ws.WorkspaceManager, ws.BaselineManager, CreateFixerCatalog(ws), verification),
            new ChangeSignatureService(ws.WorkspaceManager, resolution, ws.BaselineManager, verification));
    }

    internal static UsingDirectiveTools CreateUsingTools(ITestWorkspace ws) =>
        new(new UsingDirectiveService(ws.WorkspaceManager, ws.BaselineManager));

    internal static NavigationTools CreateNavigationTools(ITestWorkspace ws) =>
        new(CreateNavigationService(ws), CreateMethodAnalysisService(ws));

    internal static MethodAnalysisService CreateMethodAnalysisService(ITestWorkspace ws) =>
        new(new SymbolResolver(ws.WorkspaceManager), CreateReferenceService(ws), CreateNavigationService(ws));

    internal static ReferenceTools CreateReferenceTools(ITestWorkspace ws) =>
        new(CreateReferenceService(ws), new ImpactAnalysisService(new SymbolResolver(ws.WorkspaceManager)));

    internal static TypeHierarchyTools CreateTypeTools(ITestWorkspace ws) =>
        new(new TypeHierarchyService(new SymbolResolver(ws.WorkspaceManager)));

    internal static DiagnosticTools CreateDiagnosticTools(ITestWorkspace ws) =>
        new(new DiagnosticService(ws.WorkspaceManager, ws.BaselineManager, CreateFixerCatalog(ws)), ws.BaselineManager);

    internal static FixerCatalog CreateFixerCatalog(ITestWorkspace ws) =>
        new(ws.WorkspaceManager, NullLogger<FixerCatalog>.Instance);

    internal static WorkspaceTools CreateWorkspaceTools(ITestWorkspace ws) =>
        new(new WorkspaceService(ws.WorkspaceManager), new UnusedReferenceService(ws.WorkspaceManager));

    internal static NavigationService CreateNavigationService(ITestWorkspace ws) =>
        new(ws.WorkspaceManager, new SymbolResolver(ws.WorkspaceManager));

    internal static ReferenceService CreateReferenceService(ITestWorkspace ws) =>
        new(new SymbolResolver(ws.WorkspaceManager), new DiRegistrationScanner());

    /// <summary>
    ///     Minimal ShapeService.cs content with two non-implicit, used usings.
    ///     Uses System.Diagnostics (Trace) and TestFixture.Shapes (IShape).
    /// </summary>
    internal static string ShapeServiceWithDiagnostics(bool sorted = false) =>
        (sorted
            ? "using System.Diagnostics;\r\nusing TestFixture.Shapes;\r\n\r\n"
            : "using TestFixture.Shapes;\r\nusing System.Diagnostics;\r\n\r\n") +
        "namespace TestFixture.Services;\r\n\r\n" +
        "public class ShapeService\r\n{\r\n" +
        "    public IShape? Shape { get; set; }\r\n" +
        "    public void Log() => Trace.WriteLine(Shape?.Describe());\r\n}\r\n";

    // ── File path helpers ─────────────────────────────────────────────────

    internal static string CircleFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Shapes", "Circle.cs");

    internal static string ShapeFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Shapes", "Shape.cs");

    internal static string TriangleFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Shapes", "Triangle.cs");

    internal static string ShapeServiceFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Services", "ShapeService.cs");

    /// <summary>UTF-16 LE (BOM) fixture document — exercises encoding-detection rejection (F6).</summary>
    internal static string Utf16SampleFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Services", "Utf16Sample.cs");

    /// <summary>Windows-1252 (Compile-Remove'd) fixture — exercises encoding-detection rejection (F6).</summary>
    internal static string Cp1252SampleFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Services", "Cp1252Sample.cs");

    /// <summary>File-scoped-namespace fixture with multi-line verbatim/raw literals (F5).</summary>
    internal static string StringLiteralIndentFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Services", "StringLiteralIndent.cs");

    internal static string ShapeCalculatorFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Services", "ShapeCalculator.cs");

    internal static string ShapeCollectionFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Services", "ShapeCollection.cs");

    internal static string LocalFunctionExampleFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Services", "LocalFunctionExample.cs");

    internal static string UsingMixExampleFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Services", "UsingMixExample.cs");

    internal static string GlobalUsingsFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "GlobalUsings.cs");

    internal static string LegacyClassFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture.Legacy", "LegacyClass.cs");

    internal static string TypeKindExamplesFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "Services", "TypeKindExamples.cs");

    internal static string AnimalsFile(ITestWorkspace ws) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture", "MultiType", "Animals.cs");

    internal static string MultiTfmFile(ITestWorkspace ws, string fileName) =>
        Path.Combine(ws.WorkspaceManager.SolutionDirectory!, "TestFixture.MultiTfm", fileName);

    internal static string[] SplitLines(string content) =>
        content.Replace("\r\n", "\n").Split('\n');

    internal static async Task RewriteFileAsync(ITestWorkspace ws, string filePath, Func<string, string> transform)
    {
        string content = await File.ReadAllTextAsync(filePath);
        string transformed = transform(content);
        await File.WriteAllTextAsync(filePath, transformed);
        await ws.WorkspaceManager.NotifyFileChangedAsync(filePath);
    }

    /// <summary>
    ///     Rewrites every line ending in <paramref name="content" /> to CRLF (LF → CRLF, existing
    ///     CRLF left intact).
    /// </summary>
    internal static string ToCrlf(string content) =>
        content.Replace("\r\n", "\n").Replace("\n", "\r\n");

    /// <summary>
    ///     Forces the fixture file at <paramref name="filePath" /> to CRLF line endings.
    /// </summary>
    /// <remarks>
    ///     Git stores the fixture <c>.cs</c> files as LF with no <c>.gitattributes</c> rule, so a Linux
    ///     checkout gets LF while Windows (<c>core.autocrlf=true</c>) gets CRLF. Tests premised on a CRLF
    ///     file self-arrange through this helper so they hold on both platforms, mirroring how the LF
    ///     siblings arrange with <c>Replace("\r\n", "\n")</c>.
    /// </remarks>
    internal static Task EnsureCrlfAsync(ITestWorkspace ws, string filePath) =>
        RewriteFileAsync(ws, filePath, ToCrlf);

    /// <summary>
    ///     Recursively copies a directory, skipping bin/ and copying only generated
    ///     source files (*.g.cs) from obj/ subdirectories.
    /// </summary>
    internal static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);

        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
        }

        foreach (string dir in Directory.GetDirectories(source))
        {
            string dirName = Path.GetFileName(dir);
            if (dirName is "bin")
            {
                continue;
            }

            if (dirName is "obj")
            {
                CopyGeneratedSources(dir, Path.Combine(dest, dirName));
                continue;
            }

            CopyDirectory(dir, Path.Combine(dest, dirName));
        }
    }

    /// <summary>
    ///     Copies generated source files (*.g.cs, e.g. GlobalUsings.g.cs needed for implicit using
    ///     detection) and the NuGet restore metadata (<see cref="RestoreArtifactPatterns" />) from obj/
    ///     directories, preserving subdirectory structure, so package and analyzer resolution works in
    ///     the temp copy.
    /// </summary>
    private static void CopyGeneratedSources(string source, string dest)
    {
        List<string> filesToCopy = [..Directory.GetFiles(source, "*.g.cs")];
        foreach (string pattern in RestoreArtifactPatterns)
        {
            filesToCopy.AddRange(Directory.GetFiles(source, pattern));
        }

        if (filesToCopy.Count > 0)
        {
            Directory.CreateDirectory(dest);
            foreach (string file in filesToCopy)
            {
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)));
            }
        }

        foreach (string dir in Directory.GetDirectories(source))
        {
            CopyGeneratedSources(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }
}
