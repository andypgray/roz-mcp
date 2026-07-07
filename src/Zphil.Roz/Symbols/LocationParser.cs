namespace Zphil.Roz.Symbols;

/// <summary>
///     Parses the composite <c>location</c> tool parameter into a <see cref="LocationArg" />
///     subtype matching the shape required by the calling tool.
/// </summary>
/// <remarks>
///     Canonical format: <c>"path"</c>, <c>"path:line"</c>, or <c>"path:line:col"</c>. Drive
///     letters and UNC paths are preserved by walking right-to-left and only treating
///     <c>:digits</c> at the end as line/column markers. MSBuild's diagnostic format —
///     <c>"path(line)"</c> or <c>"path(line,col)"</c> — is silently accepted so locations
///     copy-pasted from <c>dotnet build</c> output round-trip without translation.
/// </remarks>
internal static class LocationParser
{
    /// <summary>
    ///     Parses a <c>location</c> argument as a file path only — rejects any
    ///     <c>:line</c> or <c>:line:col</c> suffix. Used by file-scoped tools.
    /// </summary>
    public static FileLocation ParseFile(string raw, string toolName)
    {
        (string path, int? line, int? column) = ParseRaw(raw);
        if (line.HasValue || column.HasValue)
        {
            throw new UserErrorException(
                $"{toolName} takes a file path only; remove the :line:col suffix.");
        }

        return new FileLocation(path);
    }

    /// <summary>
    ///     Parses a <c>location</c> argument that must include at least a line. Returns
    ///     <see cref="CursorLocation" /> when a column is also present, otherwise
    ///     <see cref="LineLocation" />.
    /// </summary>
    public static PositionLocation ParsePosition(string raw, string toolName)
    {
        (string path, int? line, int? column) = ParseRaw(raw);
        if (!line.HasValue)
        {
            throw new UserErrorException(
                $"{toolName} requires location with :line — e.g. 'Foo.cs:42' or 'Foo.cs:42:18'.");
        }

        return column.HasValue
            ? new CursorLocation(path, line.Value, column.Value)
            : new LineLocation(path, line.Value);
    }

    /// <summary>
    ///     Parses a <c>location</c> argument that may be a path-only file location or a
    ///     full <c>path:line:col</c> cursor. When <paramref name="treatLineOnlyAsFile" />
    ///     is true, a bare <c>path:line</c> is silently normalized to a path-only
    ///     <see cref="FileLocation" />; when false, it is rejected (the ambiguous half-cursor).
    /// </summary>
    public static LocationArg ParseFileOrCursor(string raw, string toolName, bool treatLineOnlyAsFile)
    {
        (string path, int? line, int? column) = ParseRaw(raw);
        if (!line.HasValue)
        {
            return new FileLocation(path);
        }

        if (!column.HasValue)
        {
            if (treatLineOnlyAsFile)
            {
                return new FileLocation(path);
            }

            throw new UserErrorException(
                $"{toolName} requires either a path-only location or a full 'path:line:col' cursor; " +
                "'path:line' alone is not accepted. Add the column, or drop the line and use " +
                "symbolName/containingType to disambiguate.");
        }

        return new CursorLocation(path, line.Value, column.Value);
    }

    /// <summary>
    ///     Shared parse routine: splits a raw location string into a path and optional
    ///     1-based line/column. Throws <see cref="UserErrorException" /> on malformed input
    ///     (empty, non-positive line/column, <c>:digits</c> overflow).
    /// </summary>
    private static (string Path, int? Line, int? Column) ParseRaw(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);

        string trimmed = raw.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1].Trim();
        }

        if (trimmed.Length == 0)
        {
            throw new UserErrorException("location must not be empty.");
        }

        if (TryParseMsBuildFormat(trimmed, out (string Path, int? Line, int? Column) msBuildResult))
        {
            return msBuildResult;
        }

        // Strip up to two trailing :digits segments (right-to-left). Anything else (e.g.
        // ":abc") is left attached to the path. Drive letters survive because we only
        // consume digits.
        string path = trimmed;
        int? line = null;
        int? column = null;

        if (TryStripTrailingInt(path, out string withoutLast, out int last))
        {
            // We have at least :int at the end. Try one more strip for the line.
            if (TryStripTrailingInt(withoutLast, out string withoutLastTwo, out int secondLast))
            {
                path = withoutLastTwo;
                line = secondLast;
                column = last;
            }
            else
            {
                path = withoutLast;
                line = last;
            }
        }

        if (path.Length == 0)
        {
            throw new UserErrorException("location must include a file path.");
        }

        if (line is <= 0)
        {
            throw new UserErrorException($"location line must be positive (got {line.Value}).");
        }

        if (column is <= 0)
        {
            throw new UserErrorException($"location column must be positive (got {column.Value}).");
        }

        return (path, line, column);
    }

    /// <summary>
    ///     Recognizes MSBuild's <c>"path(line)"</c> or <c>"path(line,col)"</c> diagnostic format
    ///     so locations copy-pasted from <c>dotnet build</c> output work without translation.
    ///     Returns false (and falls through to colon parsing) when the contents inside the parens
    ///     are not a clean <c>int</c> or <c>int,int</c>, which keeps file paths that legitimately
    ///     contain parentheses untouched.
    /// </summary>
    private static bool TryParseMsBuildFormat(string input, out (string Path, int? Line, int? Column) result)
    {
        result = default;

        if (input.Length < 4 || input[^1] != ')')
        {
            return false;
        }

        int openParen = input.LastIndexOf('(');
        if (openParen <= 0)
        {
            return false;
        }

        string inside = input[(openParen + 1)..^1].Trim();
        string path = input[..openParen];

        int commaIndex = inside.IndexOf(',');
        if (commaIndex < 0)
        {
            if (!Int32.TryParse(inside, out int onlyLine))
            {
                return false;
            }

            if (onlyLine <= 0)
            {
                throw new UserErrorException($"location line must be positive (got {onlyLine}).");
            }

            result = (path, onlyLine, null);
            return true;
        }

        string lineStr = inside[..commaIndex].Trim();
        string colStr = inside[(commaIndex + 1)..].Trim();

        if (!Int32.TryParse(lineStr, out int parsedLine) || !Int32.TryParse(colStr, out int parsedCol))
        {
            return false;
        }

        if (parsedLine <= 0)
        {
            throw new UserErrorException($"location line must be positive (got {parsedLine}).");
        }

        if (parsedCol <= 0)
        {
            throw new UserErrorException($"location column must be positive (got {parsedCol}).");
        }

        result = (path, parsedLine, parsedCol);
        return true;
    }

    /// <summary>
    ///     Splits "<c>{path}:{int}</c>" into <c>(path, int)</c> when the final segment is a
    ///     positive integer, otherwise returns false and leaves the path unchanged. Rejects
    ///     numeric overflow as a user error.
    /// </summary>
    private static bool TryStripTrailingInt(string input, out string remainder, out int value)
    {
        int colonIndex = input.LastIndexOf(':');
        if (colonIndex < 0 || colonIndex == input.Length - 1)
        {
            remainder = input;
            value = 0;
            return false;
        }

        string suffix = input[(colonIndex + 1)..];
        if (suffix.Length == 0 || !suffix.All(c => c >= '0' && c <= '9'))
        {
            remainder = input;
            value = 0;
            return false;
        }

        if (!Int32.TryParse(suffix, out int parsed))
        {
            throw new UserErrorException(
                $"location line/column overflowed: '{suffix}' is too large.");
        }

        remainder = input[..colonIndex];
        value = parsed;
        return true;
    }
}
