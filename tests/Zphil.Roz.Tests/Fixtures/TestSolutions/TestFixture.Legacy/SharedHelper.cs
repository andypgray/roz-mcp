namespace TestFixture.Legacy
{
    /// <summary>
    ///     Shares its simple name with TestFixture.Minimal.SharedHelper. Exists purely to test
    ///     cross-project resolution — when the same symbol name exists in multiple projects,
    ///     find_references (with or without referenceKinds=invocations) with a `project` filter must narrow
    ///     resolution to avoid ambiguity errors.
    /// </summary>
    public static class SharedHelper
    {
        public static string Greet() => "legacy";
    }
}
