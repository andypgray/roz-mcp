namespace Zphil.Roz.Presentation;

/// <summary>
///     Strips common leading whitespace from code blocks to reduce token waste in MCP responses.
/// </summary>
internal static class TextDedenter
{
    /// <summary>
    ///     Removes the common leading whitespace from all lines.
    ///     Whitespace-only lines are ignored when computing the common indent but are still dedented.
    /// </summary>
    public static string[] Dedent(string[] lines)
    {
        if (lines.Length == 0)
        {
            return lines;
        }

        int minIndent = Int32.MaxValue;
        foreach (string line in lines)
        {
            if (line.Length == 0 || line.AsSpan().IsWhiteSpace())
            {
                continue;
            }

            int indent = GetLeadingWhitespaceLength(line);
            if (indent < minIndent)
            {
                minIndent = indent;
            }
        }

        if (minIndent is 0 or Int32.MaxValue)
        {
            return lines;
        }

        var result = new string[lines.Length];
        for (var i = 0; i < lines.Length; i++)
        {
            result[i] = lines[i].Length >= minIndent
                ? lines[i][minIndent..]
                : lines[i];
        }

        return result;
    }

    private static int GetLeadingWhitespaceLength(string line)
    {
        var count = 0;
        foreach (char c in line)
        {
            if (c is ' ' or '\t')
            {
                count++;
            }
            else
            {
                break;
            }
        }

        return count;
    }
}
