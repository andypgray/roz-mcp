using System.Text;
using Zphil.Roz.Models;

namespace Zphil.Roz.Presentation;

/// <summary>
///     Formats results for using directive tools: add_usings, remove_unused_usings.
/// </summary>
internal static class UsingResultFormatter
{
    /// <summary>
    ///     Formats an add_usings result showing added, already-present, and globally-imported namespaces.
    /// </summary>
    /// <example>
    ///     <code>
    ///     Added 1 using(s) to TestFixture/Services/ShapeService.cs: System.Linq
    ///     Already present: System
    ///     </code>
    /// </example>
    public static string Format(AddUsingsResult result)
    {
        var sb = new StringBuilder();

        if (result.Added.Count > 0)
        {
            sb.AppendLine($"Added {result.Added.Count} using(s) to {result.RelPath}: {String.Join(", ", result.Added)}");
        }
        else
        {
            sb.AppendLine($"No new usings added to {result.RelPath}.");
        }

        if (result.AlreadyPresent.Count > 0)
        {
            sb.AppendLine($"Already present: {String.Join(", ", result.AlreadyPresent)}");
        }

        if (result.AlreadyGloballyImported.Count > 0)
        {
            sb.AppendLine($"Already available via global using: {String.Join(", ", result.AlreadyGloballyImported)}");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    ///     Formats a remove_unused_usings result with per-file removal details.
    /// </summary>
    /// <example>
    ///     <code>
    ///     TestFixture/Services/ShapeService.cs: removed 2 unused using(s): System.IO, System.Threading
    ///     TestFixture/Shapes/Circle.cs: no unused usings found.
    ///     </code>
    /// </example>
    public static string Format(RemoveUnusedUsingsResult result)
    {
        var sb = new StringBuilder();
        foreach (FileUsingsResult file in result.Files)
        {
            sb.AppendLine(FormatFileUsingsResult(file));
        }

        return sb.ToString().TrimEnd();
    }

    private static string FormatFileUsingsResult(FileUsingsResult result)
    {
        if (result.Error is not null)
        {
            return $"{result.RelPath}: Error: {result.Error}";
        }

        if (result.Removed.Count == 0 && result.Skipped.Count == 0)
        {
            return $"{result.RelPath}: no unused usings found.";
        }

        string line = result.Removed.Count > 0
            ? $"{result.RelPath}: removed {result.Removed.Count} unused using(s): {String.Join(", ", result.Removed)}"
            : $"{result.RelPath}: no unused usings removed.";

        string skippedSuffix = result.Skipped.Count > 0
            ? $"\n⚠ Preserved {result.Skipped.Count} using(s) in unresolved namespaces: {String.Join(", ", result.Skipped)}"
            : "";

        return $"{line}{skippedSuffix}";
    }
}
