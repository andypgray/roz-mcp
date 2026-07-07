using Microsoft.CodeAnalysis.Text;

namespace Zphil.Roz.Utility;

/// <summary>
///     Pure helper methods for extracting line ranges from <see cref="SourceText" />.
/// </summary>
internal static class SourceTextUtility
{
    /// <summary>Computes the 1-based start line number for context display.</summary>
    internal static int GetDisplayStartLine(int lineIndex, int contextLines) =>
        Math.Max(0, lineIndex - contextLines) + 1;

    /// <summary>Extracts context lines around a given line index.</summary>
    internal static string[] GetSurroundingLines(SourceText text, int lineIndex, int contextLines)
    {
        int startLine = Math.Max(0, lineIndex - contextLines);
        int endLine = Math.Min(text.Lines.Count - 1, lineIndex + contextLines);
        var lines = new string[endLine - startLine + 1];
        for (int i = startLine; i <= endLine; i++)
        {
            lines[i - startLine] = text.Lines[i].ToString();
        }

        return lines;
    }
}
