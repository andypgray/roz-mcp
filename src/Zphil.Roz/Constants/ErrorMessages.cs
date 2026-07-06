namespace Zphil.Roz.Constants;

/// <summary>
///     Shared error message templates used across services.
///     Centralizes wording to prevent drift and simplify maintenance.
/// </summary>
internal static class ErrorMessages
{
    internal static string FileNotInSolution(string filePath) =>
        $"File not found in solution: {filePath}. Use find_symbol or get_workspace_info to verify the file path.";

    internal static string CouldNotAnalyze(string filePath) =>
        $"Could not analyze {filePath} — the file may have severe syntax errors or may not be part of a C# project. Check get_diagnostics for details.";

    internal static string ProjectNotFound(string project, IEnumerable<string> available) =>
        $"No project matching '{project}' found in solution. Available projects: {String.Join(", ", available)}.";

    internal static string NotACSharpFile(string filePath, string toolName) =>
        $"'{filePath}' is not a C# source file (.cs). {toolName} only works on .cs files.";
}
