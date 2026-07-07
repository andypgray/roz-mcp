using System.Text.RegularExpressions;

namespace Zphil.Roz.StressTests.Helpers;

/// <summary>
///     Finds symbol positions (line/column) in source files by scanning for identifier tokens.
///     Eliminates fragile hardcoded line/column values that break if the source changes.
/// </summary>
internal static class SymbolPositionHelper
{
    /// <summary>
    ///     Finds the first non-comment occurrence of a symbol identifier in the file
    ///     and returns its 1-based line and column.
    ///     Matches whole words only (not substrings of other identifiers).
    /// </summary>
    internal static async Task<(int line, int column)> FindSymbolPositionAsync(string filePath, string symbolName)
    {
        string content = await File.ReadAllTextAsync(filePath);
        return FindSymbolPosition(content, symbolName);
    }

    internal static (int line, int column) FindSymbolPosition(string content, string symbolName)
    {
        var pattern = $@"\b{Regex.Escape(symbolName)}\b";

        foreach (Match match in Regex.Matches(content, pattern))
        {
            int position = match.Index;

            // Single pass: count lines and track line start up to the match position
            var line = 1;
            var lineStart = 0;
            for (var i = 0; i < position; i++)
            {
                if (content[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            // Get the text from line start to the match position
            string textBeforeMatch = content[lineStart..position];

            // Skip matches in single-line comments (// ...)
            if (textBeforeMatch.Contains("//"))
            {
                continue;
            }

            // Skip matches in string literals (basic heuristic: odd number of quotes before match on same line)
            int quoteCount = textBeforeMatch.Count(c => c == '"');
            if (quoteCount % 2 != 0)
            {
                continue;
            }

            int column = position - lineStart + 1;
            return (line, column);
        }

        throw new InvalidOperationException($"Symbol '{symbolName}' not found in non-comment code.");
    }
}
