using Xunit;

namespace DiagnosticFixture.Tests;

/// <summary>
///     Intentionally triggers xUnit1004 ("Test methods should not be skipped"),
///     a fixer-bearing analyzer warning. Used to assert that
///     <c>get_diagnostics</c> annotates output with the fixer summary block —
///     proof that <c>CompilationWithAnalyzers</c> is wired into the pipeline.
/// </summary>
public class SkippedFactExample
{
    [Fact(Skip = "Triggers xUnit1004 for fixer summary integration test")]
    public void SkippedTest()
    {
    }
}
