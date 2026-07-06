namespace DiagnosticFixture;

/// <summary>
///     Defines obsolete members for testing CS0618 diagnostics.
///     This file itself produces no diagnostics — the callers do.
/// </summary>
public static class ObsoleteApi
{
    [Obsolete("Use NewMethod instead.")]
    public static string OldMethod() => "old";

    public static string NewMethod() => "new";
}
