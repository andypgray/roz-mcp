namespace Zphil.Roz.Tests;

/// <summary>
///     Custom Shouldly extension methods for common test assertions
///     (BOM checks, line-ending validation, line searching).
/// </summary>
internal static class ShouldlyExtensions
{
    // ── Case-sensitive string overloads ──────────────────────────────────
    //
    // Shouldly's built-in ShouldContain/ShouldNotContain for strings default to
    // Case.Insensitive, which is surprising and has caused false passes and
    // unexpected failures. These overloads have fewer parameters, so C# overload
    // resolution prefers them when called without an explicit Case argument.
    // To opt into case-insensitive, callers pass Case.Insensitive explicitly,
    // which routes to Shouldly's original method.

    internal static void ShouldContain(this string actual, string expected) =>
        actual.ShouldContain(expected, Case.Sensitive);

    internal static void ShouldNotContain(this string actual, string expected) =>
        actual.ShouldNotContain(expected, Case.Sensitive);

    /// <summary>
    ///     Asserts that the string contains no bare LF characters (every \n should be part of \r\n).
    /// </summary>
    internal static void ShouldHaveNoBareLineFeed(this string content)
    {
        string withoutCrlf = content.Replace("\r\n", "");
        withoutCrlf.ShouldNotContain("\n");
    }

    /// <summary>
    ///     Asserts that the string contains no CRLF sequences (file should be LF-only).
    /// </summary>
    internal static void ShouldHaveNoCrLf(this string content) => content.ShouldNotContain("\r\n");

    /// <summary>
    ///     Asserts that the file at the given path starts with a UTF-8 BOM (EF BB BF).
    /// </summary>
    internal static async Task ShouldHaveBomAsync(this string filePath)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filePath);
        bytes.Length.ShouldBeGreaterThanOrEqualTo(3, "File too small to contain BOM");
        bytes[0].ShouldBe((byte)0xEF);
        bytes[1].ShouldBe((byte)0xBB);
        bytes[2].ShouldBe((byte)0xBF);
    }

    /// <summary>
    ///     Asserts that the file has exactly one UTF-8 BOM (EF BB BF) at the start and no additional BOMs.
    /// </summary>
    internal static async Task ShouldHaveExactlyOneBomAsync(this string filePath)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filePath);
        bytes.Length.ShouldBeGreaterThanOrEqualTo(3, "File too small to contain BOM");
        bytes[0].ShouldBe((byte)0xEF, "First BOM byte missing");
        bytes[1].ShouldBe((byte)0xBB, "Second BOM byte missing");
        bytes[2].ShouldBe((byte)0xBF, "Third BOM byte missing");

        // Verify no second BOM immediately follows
        if (bytes.Length >= 6)
        {
            bool hasSecondBom = bytes[3] == 0xEF && bytes[4] == 0xBB && bytes[5] == 0xBF;
            hasSecondBom.ShouldBeFalse("File has multiple stacked BOMs — BOM accumulation bug");
        }
    }

    /// <summary>
    ///     Asserts that the file at the given path does NOT start with a UTF-8 BOM.
    /// </summary>
    internal static async Task ShouldNotHaveBomAsync(this string filePath)
    {
        byte[] bytes = await File.ReadAllBytesAsync(filePath);
        bool hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        hasBom.ShouldBeFalse("File should not start with UTF-8 BOM");
    }

    /// <summary>
    ///     Asserts that blank lines in the using block appear only between different groups
    ///     (System vs non-System, regular vs static vs alias) and never within a group.
    /// </summary>
    internal static void ShouldHaveCorrectUsingGroupSeparation(this string content)
    {
        string[] lines = content.Replace("\r\n", "\n").Split('\n');
        int firstUsing = Array.FindIndex(lines, l => l.TrimStart().StartsWith("using "));
        int lastUsing = Array.FindLastIndex(lines, l => l.TrimStart().StartsWith("using "));

        if (firstUsing < 0 || lastUsing <= firstUsing)
        {
            return;
        }

        // Collect using lines with their group keys
        List<(int lineIndex, string line, int groupKey)> usingEntries = [];
        for (int i = firstUsing; i <= lastUsing; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("using "))
            {
                usingEntries.Add((i, trimmed, GetUsingGroupKey(trimmed)));
            }
        }

        // Check blank lines are only between groups, not within
        for (var e = 1; e < usingEntries.Count; e++)
        {
            int prevLine = usingEntries[e - 1].lineIndex;
            int currLine = usingEntries[e].lineIndex;
            bool hasBlankBetween = Enumerable.Range(prevLine + 1, currLine - prevLine - 1)
                .Any(i => String.IsNullOrWhiteSpace(lines[i]));
            bool sameGroup = usingEntries[e - 1].groupKey == usingEntries[e].groupKey;

            if (sameGroup && hasBlankBetween)
            {
                throw new ShouldAssertException(
                    $"Unexpected blank line within using group between lines {prevLine + 1} and {currLine + 1}");
            }

            if (!sameGroup && !hasBlankBetween)
            {
                throw new ShouldAssertException(
                    $"Expected blank line between using groups at lines {prevLine + 1} and {currLine + 1}");
            }
        }
    }

    private static int GetUsingGroupKey(string usingLine)
    {
        if (usingLine.Contains(" static "))
        {
            return 2; // static
        }

        // Check for alias (contains '=')
        int semiIdx = usingLine.IndexOf(';');
        string beforeSemi = semiIdx > 0 ? usingLine[..semiIdx] : usingLine;
        if (beforeSemi.Contains('='))
        {
            return 3; // alias
        }

        // Regular: System vs non-System
        string ns = usingLine.Replace("using ", "").TrimEnd(';').Trim();
        return ns == "System" || ns.StartsWith("System.") ? 0 : 1;
    }

    /// <summary>
    ///     Finds the first line matching the predicate and returns its index.
    ///     Fails with a Shouldly assertion if no line matches.
    /// </summary>
    internal static int ShouldContainLine(this string[] lines, Func<string, bool> predicate, string? customMessage = null)
    {
        int index = Array.FindIndex(lines, l => predicate(l));
        index.ShouldBeGreaterThan(-1, customMessage ?? "Expected to find a matching line but none was found");
        return index;
    }
}
