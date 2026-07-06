namespace DiagnosticFixture;

/// <summary>
///     Intentionally produces compiler warnings for diagnostic tool testing.
/// </summary>
public static class WarningExamples
{
    /// <summary>Produces CS0219: variable assigned but its value is never used.</summary>
    public static void MethodWithUnusedVariable()
    {
        int unusedLocal = 42;
    }

    /// <summary>Produces CS0618: use of obsolete member with message.</summary>
    public static string CallObsoleteMethod()
    {
        return ObsoleteApi.OldMethod();
    }
}
