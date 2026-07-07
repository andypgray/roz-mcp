namespace DiagnosticFixture;

/// <summary>
///     Intentionally produces compiler errors for diagnostic tool testing.
/// </summary>
public static class ErrorExamples
{
    /// <summary>Produces CS0246: type or namespace 'NonExistentType' could not be found.</summary>
    public static void MethodWithError(NonExistentType parameter)
    {
    }
}
